#pragma warning disable CS0618 // Application's static facade is marked obsolete in favor of a newer
// instance-based API in this v2 release; the static facade is still fully functional and is what
// the current official samples/docs use for a simple single-window app like this one.

using System.Collections.ObjectModel;
using System.ComponentModel;
using Healer.Core.Configuration;
using Healer.Setup.Logic;
using Terminal.Gui.App;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;

var answers = new WizardAnswers();
var deployDir = AppContext.BaseDirectory;

// Set by BuildComposeRestartStep once it's built; re-reads the checklist's current marks into
// `answers` right before that step is left (see OnMovingNext) — ListView has no mark-changed event
// to hook live in this Terminal.Gui version.
Action? composeRecompute = null;

// Unattended path: a box nobody ever interactively logs into (EC2 User Data, a golden AMI bake)
// can still configure and start itself at boot, as long as the operator supplied Telegram
// credentials via environment variables — this is the only way such a box gets protected at all,
// since healer-first-run.sh only fires on an interactive login that may never happen. See
// UnattendedSetup for why this always forces DryRun=true regardless of what's supplied.
if (Console.IsInputRedirected)
{
    var unattendedAnswers = UnattendedSetup.TryBuildFromEnvironment(Environment.GetEnvironmentVariable, Environment.MachineName);
    if (unattendedAnswers is null)
    {
        Console.Error.WriteLine(
            "No interactive terminal and no usable TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID environment " +
            "variables — nothing to configure unattended. Healer will offer the guided setup wizard " +
            "automatically the next time someone logs into this box interactively.");
        return 1;
    }

    return await RunUnattendedAsync(unattendedAnswers);
}

async Task<int> RunUnattendedAsync(WizardAnswers unattendedAnswers)
{
    Console.WriteLine($"No interactive terminal — configuring unattended as '{unattendedAnswers.ServerName}' (dry-run, Balanced profile).");

    var config = WizardConfigBuilder.Build(unattendedAnswers);
    await HealerInstaller.WriteConfigAsync(config, HealerInstaller.DefaultConfigPath, CancellationToken.None);
    await HealerInstaller.WriteEnvFileAsync(config.Telegram, unattendedAnswers.BotToken, unattendedAnswers.ChatId, HealerInstaller.DefaultEnvPath, CancellationToken.None);

    var unitPath = Path.Combine(deployDir, "healer.service");
    await HealerInstaller.InstallAndStartServiceAsync(unitPath, CancellationToken.None);

    var isActive = await HealerInstaller.IsServiceActiveAsync(CancellationToken.None);
    Console.WriteLine(isActive ? "Healer is installed and running (dry-run)." : "Installed, but the service doesn't look active yet — check `systemctl status healer`.");

    var (sent, error) = await TelegramTestSender.SendTestMessageAsync(unattendedAnswers.BotToken, unattendedAnswers.ChatId, unattendedAnswers.ServerName, CancellationToken.None);
    if (!sent)
    {
        Console.Error.WriteLine($"Warning: Telegram confirmation message failed to send: {error}");
    }

    return 0;
}

Application.Init();
try
{
    var wizard = new Wizard { Title = "Healer Setup" };

    var welcome = BuildWelcomeStep();
    var serverNameStep = BuildServerNameStep();
    var preflight = BuildPreflightStep();
    var telegramStep = BuildTelegramStep(out var tokenField, out var chatIdField, out var telegramStatus);
    var safetyStep = BuildSafetyStep(out var safetySelector, out var dryRunCheck, out var notifySelector);
    var scheduleStep = BuildScheduleStep(out var hostRebootCheck, out var intervalSelector, out var customDaysField, out var hourSelector);
    var composeStep = BuildComposeRestartStep();
    var reviewStep = BuildReviewStep(out var reviewLabel);
    var applyStep = BuildApplyStep(out var applyStatus);
    var successStep = BuildSuccessStep();

    wizard.AddStep(welcome);
    wizard.AddStep(serverNameStep);
    wizard.AddStep(preflight);
    wizard.AddStep(telegramStep);
    wizard.AddStep(safetyStep);
    wizard.AddStep(scheduleStep);
    wizard.AddStep(composeStep);
    wizard.AddStep(reviewStep);
    wizard.AddStep(applyStep);
    wizard.AddStep(successStep);

    wizard.MovingNext += (_, e) => OnMovingNext(wizard, e);
    wizard.StepChanged += (_, e) =>
    {
        if (e.NewValue == reviewStep)
        {
            reviewLabel.Text = BuildReviewText();
        }
        else if (e.NewValue == applyStep)
        {
            _ = ApplyAsync(applyStatus);
        }
    };

    Application.Run(wizard, null);
}
finally
{
    Application.Shutdown();
}

return 0;

// ---- step builders ----

WizardStep BuildWelcomeStep()
{
    var step = new WizardStep { Title = "Welcome", HelpText = "" };
    step.Add(new Label
    {
        X = 0, Y = 0, Width = Dim.Fill(),
        Text =
            "Healer watches this box's Docker containers and system health, and steps in\n" +
            "automatically when something needs attention — restarting a crashed container,\n" +
            "relieving memory pressure, or rebooting on a schedule you choose.\n\n" +
            "This wizard asks a few questions using menus and checkboxes — there's almost\n" +
            "nothing to type. Press Next to begin.",
    });
    return step;
}

WizardStep BuildServerNameStep()
{
    var step = new WizardStep
    {
        Title = "Name this server",
        HelpText = "Shown on every alert, so if you run Healer on more than one box you can tell\n" +
                   "at a glance which one is messaging you. We've filled in this box's hostname —\n" +
                   "keep it or change it.",
    };

    step.Add(new Label { X = 0, Y = 0, Text = "Server name:" });
    var nameField = new TextField { X = 0, Y = 1, Width = 40, Text = Environment.MachineName };
    var hint = new Label { X = 0, Y = 2, Text = "" };
    step.Add(nameField, hint);

    answers.ServerName = Environment.MachineName;
    nameField.TextChanged += (_, _) =>
    {
        answers.ServerName = nameField.Text.ToString() ?? "";
        var (valid, reason) = ServerNameValidator.Validate(answers.ServerName);
        hint.Text = valid ? "" : reason ?? "";
    };

    return step;
}

WizardStep BuildPreflightStep()
{
    var step = new WizardStep { Title = "Checking this box" };
    var list = new ListView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
    RefreshPreflight(list);
    var recheck = new Button { X = 0, Y = Pos.Bottom(list), Text = "Re-check" };
    recheck.Accepting += (_, _) => RefreshPreflight(list);
    step.Add(list, recheck);
    return step;
}

void RefreshPreflight(ListView list)
{
    var results = PreflightChecker.RunAll();
    list.SetSource(new ObservableCollection<string>(results.Select(r => r.Passed
        ? $"[OK]   {r.Name}"
        : $"[FAIL] {r.Name} — {r.DetailIfFailed}")));
}

WizardStep BuildTelegramStep(out TextField tokenField, out TextField chatIdField, out Label status)
{
    var step = new WizardStep
    {
        Title = "Connect Telegram",
        HelpText = "Healer sends alerts to your phone via a Telegram bot.\n" +
                   "Don't have one yet? Message @BotFather on Telegram, send /newbot, and copy\n" +
                   "the token it gives you. Then message @userinfobot to get your chat id.",
    };

    step.Add(new Label { X = 0, Y = 0, Text = "Bot token (from @BotFather):" });
    var localTokenField = new TextField { X = 0, Y = 1, Width = 50 };
    step.Add(localTokenField);
    var tokenHint = new Label { X = 0, Y = 2, Text = "" };
    step.Add(tokenHint);

    step.Add(new Label { X = 0, Y = 4, Text = "Chat id (from @userinfobot):" });
    var localChatIdField = new TextField { X = 0, Y = 5, Width = 30 };
    step.Add(localChatIdField);
    var chatIdHint = new Label { X = 0, Y = 6, Text = "" };
    step.Add(chatIdHint);

    var localStatus = new Label { X = 0, Y = 9, Width = Dim.Fill(), Text = "" };
    var testButton = new Button { X = 0, Y = 8, Text = "Send Test Message" };
    testButton.Accepting += async (_, _) =>
    {
        localStatus.Text = "Sending...";
        var (ok, error) = await TelegramTestSender.SendTestMessageAsync(localTokenField.Text.ToString() ?? "", localChatIdField.Text.ToString() ?? "", answers.ServerName, CancellationToken.None);
        localStatus.Text = ok ? "✅ Sent! Check your Telegram." : $"❌ Failed: {error}";
    };

    step.Add(testButton, localStatus);

    localTokenField.TextChanged += (_, _) =>
    {
        var (valid, reason) = TelegramFieldValidator.ValidateBotToken(localTokenField.Text.ToString() ?? "");
        tokenHint.Text = valid ? "" : reason ?? "";
        answers.BotToken = localTokenField.Text.ToString() ?? "";
    };
    localChatIdField.TextChanged += (_, _) =>
    {
        var (valid, reason) = TelegramFieldValidator.ValidateChatId(localChatIdField.Text.ToString() ?? "");
        chatIdHint.Text = valid ? "" : reason ?? "";
        answers.ChatId = localChatIdField.Text.ToString() ?? "";
    };

    tokenField = localTokenField;
    chatIdField = localChatIdField;
    status = localStatus;
    return step;
}

WizardStep BuildSafetyStep(out OptionSelector safety, out CheckBox dryRun, out OptionSelector notify)
{
    var step = new WizardStep { Title = "Safety profile" };

    step.Add(new Label { X = 0, Y = 0, Text = "How cautious should Healer be?" });
    var localSafety = new OptionSelector { X = 0, Y = 1, Labels = ["Conservative", "Balanced (recommended)", "Aggressive"] };
    step.Add(localSafety);
    var safetyDescription = new Label { X = 0, Y = 5, Width = Dim.Fill(), Height = 2, Text = SafetyProfilePresets.Describe(SafetyProfile.Balanced) };
    step.Add(safetyDescription);
    localSafety.Value = 1;
    localSafety.ValueChanged += (_, _) =>
    {
        var profile = (SafetyProfile)(localSafety.Value ?? 1);
        safetyDescription.Text = SafetyProfilePresets.Describe(profile);
        answers.SafetyProfile = profile;
    };

    var localDryRun = new CheckBox { X = 0, Y = 8, Text = "Start in Dry-Run mode (recommended) — Healer will alert but not act yet", Value = CheckState.Checked };
    step.Add(localDryRun);
    localDryRun.ValueChanging += (_, e) => answers.DryRun = e.NewValue == CheckState.Checked;

    step.Add(new Label { X = 0, Y = 10, Text = "How much should Healer message you on Telegram?" });
    var localNotify = new OptionSelector { X = 0, Y = 11, Labels = ["Problems only (recommended)", "Everything (verbose)"] };
    localNotify.Value = 0;
    localNotify.ValueChanged += (_, _) => answers.NotificationLevel = localNotify.Value == 1 ? NotificationLevel.Everything : NotificationLevel.ProblemsOnly;
    step.Add(localNotify);

    safety = localSafety;
    dryRun = localDryRun;
    notify = localNotify;
    return step;
}

WizardStep BuildScheduleStep(out CheckBox hostRebootCheck, out OptionSelector intervalSelector, out NumericUpDown<int> customDaysField, out OptionSelector hourSelector)
{
    var step = new WizardStep { Title = "Scheduled reboots (optional)" };

    var localEnable = new CheckBox { X = 0, Y = 0, Text = "Periodically reboot this host on a schedule" };
    step.Add(localEnable);

    step.Add(new Label { X = 0, Y = 2, Text = "How often?" });
    var localInterval = new OptionSelector { X = 0, Y = 3, Labels = RebootScheduleOptions.IntervalPresets.Select(p => p.Label).ToList() };
    step.Add(localInterval);

    var localCustomDays = new NumericUpDown<int> { X = 0, Y = 9, Value = 10 };
    var customDaysLabel = new Label { X = 0, Y = 8, Text = "Custom interval (days):", Visible = false };
    localCustomDays.Visible = false;
    step.Add(customDaysLabel, localCustomDays);

    step.Add(new Label { X = 0, Y = 11, Text = "At what local time?" });
    var localHour = new OptionSelector { X = 0, Y = 12, Labels = RebootScheduleOptions.HourPresets.Select(p => p.Label).ToList() };
    localHour.Value = 3;
    step.Add(localHour);

    var tz = TimeZoneInfo.Local;
    step.Add(new Label { X = 0, Y = 17, Text = $"Using this box's local timezone: {tz.Id} (currently {TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz):t})" });

    localInterval.ValueChanged += (_, _) =>
    {
        var isCustom = localInterval.Value == RebootScheduleOptions.IntervalPresets.Length - 1;
        customDaysLabel.Visible = isCustom;
        localCustomDays.Visible = isCustom;
    };

    void Recompute()
    {
        if (localEnable.Value != CheckState.Checked)
        {
            answers.HostRebootEnabled = false;
            return;
        }

        var idx = localInterval.Value ?? 1;
        var days = RebootScheduleOptions.IntervalPresets[idx].Days == -1 ? localCustomDays.Value : RebootScheduleOptions.IntervalPresets[idx].Days;
        var hour = RebootScheduleOptions.HourPresets[localHour.Value ?? 3].Hour;

        answers.HostRebootEnabled = true;
        answers.HostReboot = RebootScheduleOptions.Build(days, hour, 0, DayOfWeek.Sunday, tz.Id, DateTimeOffset.UtcNow);
    }

    localEnable.ValueChanging += (_, _) => Recompute();
    localInterval.ValueChanged += (_, _) => Recompute();
    localCustomDays.ValueChanged += (_, _) => Recompute();
    localHour.ValueChanged += (_, _) => Recompute();

    hostRebootCheck = localEnable;
    intervalSelector = localInterval;
    customDaysField = localCustomDays;
    hourSelector = localHour;
    return step;
}

WizardStep BuildComposeRestartStep()
{
    var step = new WizardStep
    {
        Title = "Scheduled compose refresh (optional)",
        HelpText = "Periodically restart an entire docker-compose project (all its containers\n" +
                   "together, via `docker compose restart`) — useful for picking up a slow memory\n" +
                   "leak or config drift on a schedule, separately from Healer's normal crash/\n" +
                   "overload restarts. Leave off if you don't run docker-compose here.",
    };

    var discoveredProjects = new List<DiscoveredComposeProject>();

    var localEnable = new CheckBox { X = 0, Y = 0, Text = "Periodically refresh one or more docker-compose projects" };
    step.Add(localEnable);

    var discoveryStatus = new Label { X = 0, Y = 2, Width = Dim.Fill(), Height = 1, Text = "Looking for running docker-compose projects..." };
    step.Add(discoveryStatus);

    var projectList = new ListView { X = 0, Y = 3, Width = Dim.Fill(), Height = 5, ShowMarks = true, MarkMultiple = true };
    step.Add(projectList);

    var recheck = new Button { X = 0, Y = 8, Text = "Re-check for running projects" };
    step.Add(recheck);

    var manualDirLabel = new Label { X = 0, Y = 10, Text = "No projects found — enter one manually. Directory (where its docker-compose.yml lives):" };
    var directoryField = new TextField { X = 0, Y = 11, Width = 50 };
    var directoryHint = new Label { X = 0, Y = 12, Text = "" };
    var manualNameLabel = new Label { X = 0, Y = 14, Text = "Project name (shown in alerts/history):" };
    var nameField = new TextField { X = 0, Y = 15, Width = 30 };
    step.Add(manualDirLabel, directoryField, directoryHint, manualNameLabel, nameField);

    step.Add(new Label { X = 0, Y = 17, Text = "How often?" });
    var localInterval = new OptionSelector { X = 0, Y = 18, Labels = RebootScheduleOptions.IntervalPresets.Select(p => p.Label).ToList() };
    localInterval.Value = 1;
    step.Add(localInterval);

    var localCustomDays = new NumericUpDown<int> { X = 0, Y = 24, Value = 10 };
    var customDaysLabel = new Label { X = 0, Y = 23, Text = "Custom interval (days):", Visible = false };
    localCustomDays.Visible = false;
    step.Add(customDaysLabel, localCustomDays);

    step.Add(new Label { X = 0, Y = 26, Text = "At what local time?" });
    var localHour = new OptionSelector { X = 0, Y = 27, Labels = RebootScheduleOptions.HourPresets.Select(p => p.Label).ToList() };
    localHour.Value = 3;
    step.Add(localHour);

    var tz = TimeZoneInfo.Local;
    step.Add(new Label { X = 0, Y = 32, Text = $"Using this box's local timezone: {tz.Id} (currently {TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz):t})" });

    void Recompute()
    {
        if (discoveredProjects.Count > 0)
        {
            var markedIndexes = projectList.GetAllMarkedItems().ToList();
            answers.ComposeProjects = markedIndexes.Select(i => discoveredProjects[i]).ToList();
        }
        else
        {
            answers.ComposeProjects = [];
        }

        answers.ManualComposeProjectName = nameField.Text.ToString() ?? "";
        answers.ManualComposeWorkingDirectory = directoryField.Text.ToString() ?? "";

        if (localEnable.Value != CheckState.Checked)
        {
            answers.ComposeRestartEnabled = false;
            return;
        }

        var idx = localInterval.Value ?? 1;
        var days = RebootScheduleOptions.IntervalPresets[idx].Days == -1 ? localCustomDays.Value : RebootScheduleOptions.IntervalPresets[idx].Days;
        var hour = RebootScheduleOptions.HourPresets[localHour.Value ?? 3].Hour;

        answers.ComposeRestartEnabled = true;
        answers.ComposeRestartSchedule = RebootScheduleOptions.Build(days, hour, 0, DayOfWeek.Sunday, tz.Id, DateTimeOffset.UtcNow);
    }

    async Task RefreshProjectsAsync()
    {
        discoveryStatus.Text = "Looking for running docker-compose projects...";
        var discovered = await ComposeProjectDiscovery.DiscoverRunningProjectsAsync("/var/run/docker.sock", CancellationToken.None);
        discoveredProjects.Clear();
        discoveredProjects.AddRange(discovered);

        var showManualFallback = discovered.Count == 0;
        projectList.Visible = !showManualFallback;
        manualDirLabel.Visible = showManualFallback;
        directoryField.Visible = showManualFallback;
        directoryHint.Visible = showManualFallback;
        manualNameLabel.Visible = showManualFallback;
        nameField.Visible = showManualFallback;

        discoveryStatus.Text = showManualFallback
            ? "No running docker-compose projects found. Start them with `docker compose up -d`, then\npress \"Re-check\" — or enter one manually below, or leave this off for now."
            : $"Found {discovered.Count} running project(s) — press Space to check the ones to refresh:";

        projectList.SetSource(new ObservableCollection<string>(discovered.Select(p => $"{p.Name}  ({p.WorkingDirectory})")));
        Recompute();
    }

    recheck.Accepting += (_, _) => _ = RefreshProjectsAsync();
    _ = RefreshProjectsAsync();

    directoryField.TextChanged += (_, _) =>
    {
        var directory = directoryField.Text.ToString() ?? "";
        var (valid, reason) = ComposeProjectDirectoryValidator.Validate(directory);
        directoryHint.Text = valid ? "" : reason ?? "";

        if ((nameField.Text.ToString() ?? "").Length == 0)
        {
            nameField.Text = ComposeProjectDirectoryValidator.DeriveProjectName(directory);
        }
    };

    localInterval.ValueChanged += (_, _) =>
    {
        var isCustom = localInterval.Value == RebootScheduleOptions.IntervalPresets.Length - 1;
        customDaysLabel.Visible = isCustom;
        localCustomDays.Visible = isCustom;
    };

    localEnable.ValueChanging += (_, _) => Recompute();
    directoryField.TextChanged += (_, _) => Recompute();
    nameField.TextChanged += (_, _) => Recompute();
    localInterval.ValueChanged += (_, _) => Recompute();
    localCustomDays.ValueChanged += (_, _) => Recompute();
    localHour.ValueChanged += (_, _) => Recompute();

    // Marking/unmarking a project in the checklist (Space bar) has no change event to hook in this
    // Terminal.Gui version — GetAllMarkedItems() is read on demand instead, right before this step
    // is left (see OnMovingNext), which is the only point the marks actually need to be current by.
    composeRecompute = Recompute;

    return step;
}

WizardStep BuildReviewStep(out Label label)
{
    var step = new WizardStep { Title = "Review" };
    var localLabel = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Text = "" };
    step.Add(localLabel);
    label = localLabel;
    return step;
}

string BuildReviewText()
{
    var lines = new List<string>
    {
        $"Server name: {answers.ServerName} (this prefixes every Telegram message)",
        answers.DryRun
            ? "Healer will run in SAFE (dry-run) mode — it will message you about problems but NOT restart or reboot anything yet."
            : "Healer will run LIVE — it will actually restart containers and take pressure-relief actions when needed.",
        $"Safety profile: {answers.SafetyProfile}",
        $"Telegram notifications: {(answers.NotificationLevel == NotificationLevel.Everything ? "Everything" : "Problems only")}",
        answers.HostRebootEnabled && answers.HostReboot is { } schedule
            ? $"Scheduled host reboot: every {schedule.IntervalDays} day(s) at {schedule.Hour:D2}:{schedule.Minute:D2} ({schedule.TimeZoneId})"
            : "Scheduled host reboot: disabled",
answers.ComposeRestartEnabled && answers.ComposeRestartSchedule is { } composeSchedule
            ? $"Scheduled compose refresh: {DescribeComposeProjects(answers)} every {composeSchedule.IntervalDays} day(s) at {composeSchedule.Hour:D2}:{composeSchedule.Minute:D2} ({composeSchedule.TimeZoneId})"
            : "Scheduled compose refresh: disabled",
        "",
        "Press Next to install and start Healer with these settings.",
    };
    return string.Join("\n", lines);
}

string DescribeComposeProjects(WizardAnswers a)
{
    if (a.ComposeProjects.Count > 0)
    {
        return string.Join(", ", a.ComposeProjects.Select(p => $"'{p.Name}'"));
    }

    return $"'{a.ManualComposeProjectName}' ({a.ManualComposeWorkingDirectory})";
}

WizardStep BuildApplyStep(out Label status)
{
    var step = new WizardStep { Title = "Applying" };
    var localStatus = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), Text = "Press Next to apply." };
    step.Add(localStatus);
    status = localStatus;
    return step;
}

async Task ApplyAsync(Label status)
{
    status.Text = "Writing configuration...";
    var config = WizardConfigBuilder.Build(answers);
    await HealerInstaller.WriteConfigAsync(config, HealerInstaller.DefaultConfigPath, CancellationToken.None);
    await HealerInstaller.WriteEnvFileAsync(config.Telegram, answers.BotToken, answers.ChatId, HealerInstaller.DefaultEnvPath, CancellationToken.None);

    status.Text = "Installing the background service...";
    var unitPath = Path.Combine(deployDir, "healer.service");
    await HealerInstaller.InstallAndStartServiceAsync(unitPath, CancellationToken.None);

    status.Text = "Verifying it's running...";
    var isActive = await HealerInstaller.IsServiceActiveAsync(CancellationToken.None);
    status.Text = isActive ? "✅ Healer is installed and running." : "⚠ Installed, but the service doesn't look active yet — check `systemctl status healer`.";
}

WizardStep BuildSuccessStep()
{
    var step = new WizardStep { Title = "Done" };
    step.Add(new Label
    {
        X = 0, Y = 0, Width = Dim.Fill(),
        Text = "Healer is set up. You'll get a Telegram message confirming it's running.\n\n" +
               "Run `healer-status` any time to see current health and history.",
    });
    return step;
}

void OnMovingNext(Wizard wizard, CancelEventArgs e)
{
    var current = wizard.CurrentStep;

    if (current?.Title == "Name this server")
    {
        var (nameOk, nameReason) = ServerNameValidator.Validate(answers.ServerName);
        if (!nameOk)
        {
            e.Cancel = true;
            MessageBox.ErrorQuery(Application.Instance, "Server name needed", nameReason ?? "Please enter a name.", "OK");
        }
    }
    else if (current?.Title == "Connect Telegram")
    {
        var (tokenOk, tokenReason) = TelegramFieldValidator.ValidateBotToken(answers.BotToken);
        var (chatOk, chatReason) = TelegramFieldValidator.ValidateChatId(answers.ChatId);
        if (!tokenOk || !chatOk)
        {
            e.Cancel = true;
            MessageBox.ErrorQuery(Application.Instance, "Check Telegram details", tokenReason ?? chatReason ?? "Please fix the highlighted field(s).", "OK");
        }
    }
    else if (current?.Title == "Scheduled compose refresh (optional)")
    {
        composeRecompute?.Invoke();

        if (answers.ComposeRestartEnabled && answers.ComposeProjects.Count == 0)
        {
            var (dirOk, dirReason) = ComposeProjectDirectoryValidator.Validate(answers.ManualComposeWorkingDirectory);
            var (nameOk, nameReason) = ServerNameValidator.Validate(answers.ManualComposeProjectName);
            if (!dirOk || !nameOk)
            {
                e.Cancel = true;
                MessageBox.ErrorQuery(Application.Instance, "Check compose project details", dirReason ?? nameReason ?? "Please fix the highlighted field(s), or check at least one project above.", "OK");
            }
        }
    }
}
