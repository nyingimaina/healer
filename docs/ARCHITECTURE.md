# Architecture

## Why this shape

Healer exists because on resource-constrained EC2 boxes, two failure modes recur: Docker containers
crash (often OOM-killed because most projects set no `mem_limit`), and the whole Linux host can
collapse when the kernel's OOM-killer picks an arbitrary victim — sometimes `sshd` or `dockerd`
itself. Everything here is designed around that: detect early, act conservatively and predictably,
never let the healer become part of the problem, and never let alerting become noise you learn to
ignore.

## Core / Host / Setup / Status split

`Healer.Core` holds every piece of decision logic — threshold evaluation, backoff/circuit-breaker,
scheduling, retention cutoffs, the engine that composes them — and touches no Docker socket, no
`/proc`, no network, no disk beyond what's injected via interfaces. This is deliberate: the
safety-critical logic is fully unit-testable (135 tests across the solution) without any of that
infrastructure, and via `Microsoft.Extensions.TimeProvider.Testing` it's deterministic even for
time-based logic like backoff stages and schedules.

`Healer.Host` is the always-on daemon: Native AOT, implements every `Healer.Core.Abstractions`
interface against the real Docker Engine API (over the Unix socket), `/proc`, SQLite, Telegram, and
systemd. `Healer.Setup` and `Healer.Status` are one-shot/on-demand tools built on Terminal.Gui —
not under the daemon's AOT/footprint constraints, so they can afford Terminal.Gui's reflection use
and a bigger self-contained binary.

## The dry-run guarantee

`HealingEngine.RunTickAsync` always calls every *read* method (metrics, container list) — detection
must always happen — but every *mutating* call is gated behind `if (!config.DryRun)`. This isn't a
convention callers have to remember; it's structural. `HealingEngineTests` asserts a full scripted
multi-incident dry-run scenario produces zero calls into any mutating fake.

## Telegram noise control

Every notification passes through `HealingEngine.ShouldSendToTelegram`, which — under the default
`ProblemsOnly` level — sends failures, circuit-opens, dry-run previews, and reboots, but stays quiet
about Healer routinely and successfully restarting something. The reasoning: a channel that pings
you for every routine success trains you to stop reading it, which defeats the point of alerting at
all. Routine successes are still recorded to the SQLite history and visible in `Healer.Status` —
this only controls what interrupts your phone. Every message is also prefixed with `config.ServerName`
(`MessageFormatter.WithServerPrefix`) since one Telegram bot/chat commonly serves several boxes.

## Docker integration: hand-rolled, not Docker.DotNet

`Docker.DotNet`'s Native AOT/trimming support is unproven. `Healer.Host.Docker.DockerApiClient` is a
thin `HttpClient` configured with a `SocketsHttpHandler.ConnectCallback` that dials the Unix domain
socket directly, paired with `System.Text.Json` source-generated (de)serialization — both fully
AOT-safe. Every Docker Engine API path accepts a container's name or id interchangeably, so no
name-to-id resolution step is needed.

## A real bug worth understanding: source-generated JSON and property defaults

**System.Text.Json's source-generated deserializer silently discards C# property initializer
defaults for anything missing from the JSON**, for ordinary `{ get; init; } = value` properties —
confirmed empirically during development. A config record like:

```csharp
public sealed record ThresholdsConfig
{
    public double HostMemoryCriticalPercent { get; init; } = 90;
}
```

would deserialize `HostMemoryCriticalPercent` to `0.0`, not `90`, if `"hostMemoryCriticalPercent"`
were absent from the JSON — a live safety hazard for a threshold-driven system (a hand-edited or
partially-outdated config could silently zero out a safety threshold). Reflection-based
`JsonSerializer.Deserialize<T>()` does **not** have this bug — only the source-generated path does,
and Native AOT requires the source-generated path.

**The fix**: every record in `Healer.Core.Configuration` is a *positional* record with constructor
parameter defaults, not `{ get; init; } = value` properties. The source generator correctly falls
back to a constructor parameter's default value when its JSON key is missing. Reference-type
defaults that aren't compile-time constants (a nested config's `new()`, a list literal, `TimeZoneInfo
.Local.Id`) use a nullable positional parameter plus a body-redeclared property computing the real
default (`Thresholds ?? new()`). Genuinely required fields (`ServerName`, `NamePattern`, the
`RebootSchedule` fields) are marked `required` in a body redeclaration backed by the positional
parameter — this makes the source-generated deserializer correctly *throw* if they're missing,
rather than silently defaulting, while a harmless `""`/default constructor-parameter default keeps
ordinary C# object-initializer construction syntax (`new HealerConfig { ServerName = "x" }`) working
everywhere the codebase already used it.

`Healer.Host.Tests/HealerJsonRoundTripTests.cs` is a regression suite for exactly this: it
deserializes a minimal config through the *real* `HealerJsonContext`, not just via C# object
construction, and asserts every default survives correctly — including against the actual shipped
`deploy/healer.config.sample.json`.

## Guaranteeing the daemon survives reboots (and hangs)

`Restart=always` alone is not a full guarantee: systemd's default restart-rate limit can give up
entirely on a genuine crash-loop, and `Restart=always` only catches a process that *exits* — not one
that hangs while technically still running. `deploy/healer.service` closes both gaps:

- `StartLimitIntervalSec=0` disables the restart-rate limit entirely, so systemd never gives up.
- `Type=notify` + `WatchdogSec=90`, paired with `HostActions/SystemdNotifier.cs` calling `sd_notify
  READY=1` once at startup and `WATCHDOG=1` every tick — a genuine hang (not just a crash) gets
  detected and the process killed/restarted.
- `OOMScoreAdjust=-900` makes the kernel OOM-killer one of the last things to ever consider killing
  Healer itself — it would be a bad joke if the tool built to prevent OOM kills were an easy target.

**Explicit boundary**: this guarantees survival across crashes, hangs, and host reboots — it does
**not** protect against total EC2 instance termination, which needs an EC2-level mechanism (Auto
Recovery, an Auto Scaling Group) outside a host-level daemon's reach.

### Confirming a reboot actually worked, not just assuming it did

`ExecuteChosenActionAsync`'s `ScheduledHostReboot` branch has always had an honesty gap: it records a
`Success` outcome and sends the Telegram notice *before* calling `RebootHostAsync`, simply because
the process doesn't survive to report anything afterward. That's a reasonable guess, not a real
confirmation — the box might not come back up cleanly, or `healer` might fail to restart even if the
box does.

**`Healer.Core.Decision.RebootVerifier`** closes this, for both the production scheduled-reboot path
and the setup wizard's "Test Reboot Now" button (see below): whatever triggers a reboot sets
`HealerState.PendingRebootRequestedUtc`/`PendingRebootReason` right before rebooting; every tick
afterward, `HealingEngine.MaybeVerifyPendingRebootAsync` compares that timestamp against
`HostMetrics.BootTimeUtc` (read from `/proc/uptime` — the only reliable signal that the *host*
actually rebooted, as opposed to just the `healer` service restarting). Three outcomes: boot time is
now after the request → **Verified**, record + notify `Success` and clear the marker; the 30-minute
grace window elapsed with boot time never advancing → **TimedOut**, record + notify `Failed` and
clear the marker (Healer came back up, but the host apparently never rebooted — or this is a later,
unrelated daemon start that found a stale marker); still within the window with boot time unchanged →
say nothing yet, don't spam every 15-second tick. Recorded as a new `ActionType.HostRebootVerification`
— deliberately not reusing `ScheduledHostReboot`, since "we decided to reboot" and "we confirmed a
reboot actually completed" are different events that would otherwise look identical in
`healer-status`'s history browser.

**The wizard's "Test Reboot Now"** (`Healer.Setup.Logic.RebootTestRunner`, on the Success step, after
Apply) reuses this exact mechanism rather than inventing its own — the wizard process can't report
success itself for the same reason `ExecuteChosenActionAsync` never could, so it just requests a
reboot the same way the production path does and lets the daemon's own verification report back via
Telegram once it's actually back up. A genuine race had to be closed here: by the time this button is
reachable, `healer` is already running (Apply already did `enable --now`), so writing the pending-
reboot marker straight into the state file risks the live daemon's own next periodic `SaveAsync` —
using its in-memory copy, which knows nothing of the edit — silently clobbering it moments later.
`RebootTestRunner` avoids this by stopping the service, editing the state file while nothing else can
touch it, and restarting the service (so it loads the fresh marker into memory) *before* triggering
the actual reboot — starting a service doesn't itself advance the host's boot time, so this can't
produce a false-positive "verified" the instant it restarts.

**Explicit limitation, not glossed over**: if the box never comes back from a reboot test at all,
nothing running on it can report that — the only signal is the *absence* of the expected Telegram
message, not a positive failure report. `TimedOut` only fires for the narrower case where Healer
itself does start back up but boot time never advanced.

## History storage and retention

`Healer.Host.History.SqliteHistoryStore` uses plain parameterized ADO.NET via `Microsoft.Data.Sqlite`
— not EF Core, whose LINQ/query-translation path isn't reliably AOT-safe. `journal_mode=WAL` +
`synchronous=NORMAL` so the history DB survives the exact kind of mid-write crash Healer exists to
guard against; `auto_vacuum=INCREMENTAL` plus a periodic `PRAGMA incremental_vacuum` because a plain
`DELETE`-based retention policy alone does **not** shrink the file, only free pages inside it. Two
retention tiers: 15-day raw snapshots (high-volume, low individual value), 90-day action/incident
history (low-volume, high diagnostic value) — see `Healer.Core.History.RetentionCutoffCalculator`.

The `actions` table (and `healer-status`'s "Recent incidents / actions" panel, which reads it via
`QueryActionsAsync` — `ORDER BY ts DESC` already, newest first) is generic across every
`ActionType`: `HealingEngine.NotifyAndRecordAsync` calls `RecordActionAsync` for every chosen action
regardless of type, so scheduled host reboots and scheduled compose restarts show up there exactly
the same way crash-loop/worst-offender restarts do, with no per-type wiring needed on the
`Healer.Status` side — confirmed directly for compose restarts in
`HealingEngineTests.Live_ScheduledComposeRestart_ExecutesAndPersistsLastRun`.

## Staging vs. configuring — three ways a box ends up protected

Installing and configuring are deliberately separable, because they don't always happen in the same
place: `bootstrap.sh` can run interactively (someone SSHed in) or non-interactively (EC2 User Data
at boot, a golden AMI bake) — and an interactive TUI wizard cannot run with no terminal attached.
Staging (copying binaries, symlinking `healer-setup`/`healer-status` onto `PATH`, installing
`/etc/profile.d/healer-first-run.sh`) never writes `/etc/healer/healer.json` or starts the systemd
service — only actually completing setup does.

`Healer.Setup`'s `Program.cs` decides what "completing setup" means the moment it runs, based on
`Console.IsInputRedirected`:

1. **A real terminal is attached** → the guided TUI wizard, as designed for a human.
2. **No terminal, but `TELEGRAM_BOT_TOKEN`/`TELEGRAM_CHAT_ID` (and optionally `HEALER_SERVER_NAME`)
   are set as environment variables** (`Healer.Setup.Logic.UnattendedSetup`) → configures and starts
   itself immediately, unattended, always forcing `DryRun=true` and the Balanced profile regardless
   of what's supplied — a fleet-wide automated rollout must never silently start taking live action
   without a human reviewing at least one instance of it first. **This is the only path that
   protects a box nobody ever logs into** — a box provisioned once and left alone precisely because
   it's running fine is exactly the case a login-triggered mechanism can never reach.
3. **No terminal, no usable credentials** → prints guidance and exits. `healer-first-run.sh` is the
   fallback: it fires on every interactive login (guarded by `[ -t 0 ]` so it never intrudes on
   non-interactive sessions like `scp` or Ansible) and launches `healer-setup` automatically as long
   as `/etc/healer/healer.json` doesn't exist yet.

All three converge through the same `bootstrap.sh` invocation — it always runs `healer-setup` and
lets it decide, rather than branching in shell. This also makes an interrupted session safe by
construction: nothing is written until setup (wizard or unattended) actually completes, so a dropped
SSH connection or a User Data run with no credentials just leaves the box inert until the next
interactive login offers the wizard again.

**A subtlety worth knowing if you touch `bootstrap.sh`**: when it's run as `curl -fsSL <url> | sh`,
the script's own stdin is the pipe *from curl*, not the caller's terminal — a well-known trap with
piped installers. Left alone, `Console.IsInputRedirected` would read that pipe as "non-interactive"
and skip the wizard even for someone running the one-liner live at their keyboard. The fix (the same
one rustup/nvm/etc. use) is re-attaching stdin to `/dev/tty` — the process's controlling terminal,
independent of what fd 0 is redirected to — before `exec`ing `healer-setup`, falling through
unchanged when `/dev/tty` can't be opened at all (genuinely no controlling terminal, e.g. real EC2
User Data).

### A fourth path: a single `.deb` package (Ubuntu only)

`deploy/build-deb.sh` packages the same three binaries plus `healer.service`/`healer-first-run.sh`
into one native Ubuntu package (built via WSL — see `docs/DEPLOY-FROM-WINDOWS.md` Option C), whose
`postinst` is, again, a thin wrapper that just runs `/opt/healer/healer-setup` and lets it decide —
the exact same `Console.IsInputRedirected` branching, reused completely unchanged. `healer.service`
is deliberately shipped inside `/opt/healer/` alongside the binaries rather than at the
Debian-canonical `/lib/systemd/system/`, specifically so `HealerInstaller`'s existing
`File.Copy(...)` + `systemctl enable --now` logic keeps working exactly as it does for the
bootstrap.sh/tarball path — zero application code was changed to support this distribution
mechanism. The one consequence: `/etc/systemd/system/healer.service` is never dpkg-tracked, so
`postrm` removes it explicitly on both `remove` and `purge`, alongside Debian's standard
remove-keeps-data / purge-deletes-data split for `/etc/healer`, `/var/lib/healer`, `/var/log/healer`.

A `.deb`'s `postinst` running a full interactive TUI is a deliberate policy deviation (Debian policy
generally prefers debconf for exactly this reason), accepted here because `healer-setup` never
blocks waiting for input in a context with no usable terminal — it falls straight through to the
unattended or stage-and-exit paths instead. The one real gap this reintroduces: `dpkg -i` run via a
single non-pty remote command (`ssh host "sudo dpkg -i file.deb"`, as opposed to an interactive
session) doesn't get a real terminal either, and would silently take the non-interactive branch —
`deploy/deploy-to-ec2.sh`/`Deploy-ToEc2.ps1` avoid this by using `ssh -t` when offering to install
remotely, the same `/dev/tty`-adjacent fix in spirit as `bootstrap.sh`'s own curl-pipe workaround.

**Two real bugs found and fixed by actually installing the package**, not just building it — worth
understanding before touching `build-deb.sh` or `healer.service` again:

- **`healer-setup`/`healer-status` need `PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`,
  not just `--self-contained true`.** Without it, publish produces ~220 loose files (the apphost
  plus every runtime/dependency DLL and native library); copying only the named executable — the
  obvious thing to do — silently ships a binary missing everything it needs to run. Fixed by adding
  both properties, which bundle everything into one real executable.
- **A Native AOT executable does NOT get the "search alongside the executable for native
  libraries" behavior that ordinary self-contained .NET apps get.** That resolution logic lives in
  the corehost/apphost layer, which Native AOT bypasses by design — Native AOT only compiles
  *managed* code to native; a genuinely native dependency like SQLite's C library still ships as
  its own `.so` file next to `healer` (confirmed by inspecting `publish/host/` — `dotnet publish`
  produces `libe_sqlite3.so` there unprompted), but the OS's dynamic linker was never told to look
  in `/opt/healer/` for it. Surfaced at runtime as `DllNotFoundException: Unable to load shared
  library 'e_sqlite3'` on every history-recording tick — the daemon started and ran fine; only
  SQLite access failed, since that's the only genuinely-native P/Invoke dependency in the daemon.
  Fixed with `Environment=LD_LIBRARY_PATH=/opt/healer` in `healer.service`, set as part of the
  process's environment before the dynamic linker starts — setting it from inside a running
  process would NOT reliably work, since glibc caches the library search path at process startup,
  not per-`dlopen()`-call. `healer-setup`/`healer-status` don't need this: their
  `IncludeNativeLibrariesForSelfExtract` bundling has .NET's own (different, tested) native-library
  search-path setup built in, which is specific to the single-file host and unrelated to AOT.

This second bug also exposed a real gap in the `postinst` upgrade branch: since `healer.service` is
copied at runtime (not dpkg-tracked, per above), and the upgrade branch only ran `systemctl
try-restart`, a newer package version changing the unit file — exactly this fix — would never
actually reach an already-configured box, on a real version upgrade or a same-version reinstall
alike. Fixed by having the upgrade branch also re-copy `/opt/healer/healer.service` to
`/etc/systemd/system/` and `daemon-reload` before restarting, whenever the unit is already present.

**These same bugs also shipped, independently, in the GitHub Actions tarball path
(`.github/workflows/release.yml` + `bootstrap.sh`)** — a real install via that path failed with
`The application to execute does not exist: '/opt/healer/healer-setup.dll'` (the missing
`PublishSingleFile` bug) even though `build-deb.sh` had already been fixed. The two packaging paths
had simply drifted apart: `build-deb.sh` got all three fixes above when they were found, but nobody
had ported the same three changes to `release.yml`, which builds its own tarball independently
rather than reusing `build-deb.sh`. `release.yml`'s `Publish Healer.Setup`/`Healer.Status` steps and
its `Package release tarball` step now carry the identical `PublishSingleFile` +
`IncludeNativeLibrariesForSelfExtract` + `.bin`-rename-plus-wrapper-script +
`libe_sqlite3.so`-copy treatment, verbatim in spirit if not in exact shell syntax (the release
workflow builds the wrapper scripts with `printf`, not a heredoc, since a `<<EOF` heredoc's closing
delimiter must have zero leading whitespace — awkward to guarantee inside an indented YAML `run: |`
block). **The lesson, not just the fix**: two independent packaging scripts for the same three
binaries is a real maintenance hazard — a fix applied to one silently does not apply to the other.

**That "third path" warning turned out to already exist and already have drifted**: the exact same
three-part bug (no `PublishSingleFile`, no `libe_sqlite3.so`, no extraction-dir wrapper) was also
present, independently, in `docs/DEPLOY-FROM-WINDOWS.md`'s "Option B" manual copy-paste
instructions — a THIRD hand-maintained copy of the same publish/package logic that nobody had
walked through end-to-end either. Fixed the same way. At three independent copies of this logic
(`build-deb.sh`, `release.yml`, and now a markdown doc), the case for actually extracting a single
shared "assemble the payload" script all three call — rather than a fourth copy-paste — is no
longer hypothetical.

**A fourth real bug, found the same way (a real install, not code review): `/usr/local/bin` is not
reliably on every box's `PATH`, or on `sudo`'s own `secure_path`.** A real EC2 box reported
`healer-status: command not found` for both a plain and a `sudo`-prefixed invocation, immediately
after a successful install, even though `/usr/local/bin/healer-status` existed as a valid symlink —
only the full absolute path worked. `/usr/local/bin` being on `PATH` is a *convention*, not a
guarantee, and evidently isn't universal across every AMI/hardening profile. `/usr/bin` is: it's
where core system commands themselves live, so every shell's `PATH` and every `sudo`
`secure_path` includes it unconditionally, with no known exceptions. All three packaging paths
(`build-deb.sh`, `bootstrap.sh`, and the Option B doc) now symlink there instead — which also
happens to resolve a pre-existing, previously-accepted Debian Policy §9.1.2 deviation (packages
aren't supposed to install into `/usr/local` at all, which is reserved for the sysadmin's own,
non-package-managed installs) as a side effect of fixing the real bug.

## Scheduled docker-compose restarts

A second, independent scheduling mechanism alongside scheduled host/container reboots
(`ScheduledReboots`): `ScheduledComposeRestartsConfig.Projects`, a list of
`ComposeProjectSchedule(ProjectName, WorkingDirectory, Schedule)` entries, where `Schedule` reuses
`RebootSchedule` verbatim (same interval-in-days/anchor/hour/timezone shape, same catch-up-skips-a-
missed-window policy via `ScheduleMatcher.IsDue` — no new matching logic was needed).

**Why `docker compose restart` (a CLI subprocess) instead of the existing Docker Engine API client**:
Healer already talks to `/var/run/docker.sock` directly for everything else (see "Docker
integration" above), specifically to avoid a dependency on the `docker` CLI being installed. Compose
is the one exception — its dependency-aware restart ordering (respecting `depends_on`) lives
entirely inside the `docker compose` CLI/plugin, with no equivalent Engine API endpoint. Reproducing
that ordering logic by hand over the raw API would be substantial, redundant work for something the
CLI already does correctly. `Healer.Host.Docker.DockerComposeRestartExecutor` shells out to
`docker compose restart` (via `System.Diagnostics.Process`, `WorkingDirectory` set to the
configured project directory) rather than duplicating compose's own orchestration.

**v1 (`docker-compose`) vs. v2 (`docker compose`) detection**: Docker Compose ships two
incompatible invocation shapes — the current CLI plugin, run as a `docker` subcommand (`docker
compose ...`), and the older standalone binary some existing boxes still have instead (`docker-compose
...`, hyphenated, no `docker` prefix). A box with only v1 installed has no `docker compose`
subcommand at all, so hardcoding the v2 shape would silently break this feature there.
`DockerComposeRestartExecutor` probes for both — `docker compose version` first, then
`docker-compose version` — on every restart call (not cached, since this only runs on a
weekly/monthly schedule, not per-tick, so the cost of re-probing is irrelevant, and it means a
Compose upgrade takes effect without a daemon restart) and picks whichever succeeds via
`ResolveComposeCommand`, a pure `internal` function taking two plain `bool`s specifically so it's
unit-testable without mocking process execution — the actual probing stays untested I/O, consistent
with the rest of `Healer.Host`'s thin wrappers. If neither is available, it throws a clear error
naming both things it tried, which flows through the existing dry-run/live action-outcome pipeline
exactly like any other failed action (a `Failed` entry in Telegram/`healer-status` history) — no
special-case handling needed.

**Why all-at-once, not staggered like other restarts**: every other restart path in
`HealingEngine` (crash-loop, critical-threshold, pre-emptive worst-offender) restarts exactly one
container at a time, gated by `CooldownGate`'s per-tick concurrency cap — deliberately, since those
are reactive and multiple simultaneous ones would compound an already-bad situation. A *scheduled*
compose refresh is different: it's planned, not reactive, and `docker compose restart` restarting
everything together is exactly what a person would do by hand and exactly what compose is designed
to do safely (it already handles inter-service ordering). Staggering it container-by-container
would fight compose's own dependency graph for no safety benefit, so `ScheduledComposeRestart` is
deliberately NOT added to `HealingEngine.TargetsAContainer` — it bypasses the per-container
backoff/circuit-breaker machinery entirely, the same way `ScheduledHostReboot` and
`HostPressureRelief` already do, since none of those target one container's own health history.

**Exclusions reuse `containerOverrides`, not a separate list**: a new
`ContainerOverrideConfig.ExcludeFromPeriodicComposeRestart` flag (default `false`) sits alongside
the existing `ExcludeFromWorstOffenderSelection` — e.g. a database container nobody wants casually
bounced on a timer. Since `docker compose restart` has no "all services except N" syntax, an
exclusion forces `DockerComposeRestartExecutor` to first run `docker compose config --services` to
enumerate the project's real service names, then pass every non-excluded one explicitly; with no
exclusions configured, it's a plain `docker compose restart` (no service arguments), so a service
added to the compose file later is picked up automatically without touching Healer's config.

**The `healer-setup` wizard step** ("Scheduled compose refresh (optional)", mirroring "Scheduled
reboots" — same `RebootScheduleOptions` interval/hour presets, same `OnMovingNext` validation-gate
pattern) finds the project directory itself rather than asking anyone to type or know a filesystem
path — the governing rule for this whole wizard is that typing is the last resort, and a raw path is
exactly the kind of thing a non-technical operator running someone else's compose stack on a fresh
EC2 box would have no way to answer.

**How discovery works** (`Healer.Setup.Logic.ComposeProjectDiscovery`): `docker compose up` stamps
every container it starts with `com.docker.compose.project` and
`com.docker.compose.project.working_dir` labels (the same mechanism `docker compose ls` itself
reads internally). `DiscoverRunningProjectsAsync` lists running containers via the Docker Engine API
and groups them by that first label, taking the second as the directory — no path is ever typed for
a project that's already running, which covers the overwhelmingly common case (the operator is
installing Healer onto a box where the actual application is already deployed and running). Healer
had no Docker integration inside `Healer.Setup` at all before this — it now takes a `ProjectReference`
on `Healer.Host` specifically to reuse the existing hand-rolled `DockerApiClient` Unix-socket
transport rather than duplicating that plumbing; this is a plain library reference and doesn't put
`Healer.Setup` under Healer.Host's AOT/trim publish constraints (those are set on Healer.Host's own
`dotnet publish` invocation, not inherited by anything that merely references its assembly).

The step shows discovered projects as a check-off list (`ListView` with `ShowMarks`/`MarkMultiple` —
Space to check/uncheck, Enter never required) and lets more than one be selected, all sharing the one
schedule configured on the same screen — one interval covers "refresh everything periodically",
which is what was actually asked for, rather than forcing a separate schedule per project. **A typed
fallback still exists** (`ComposeProjectDirectoryValidator`, validated for existence and a real
compose file, with the project name auto-derived from the directory's folder name) but only appears
when discovery finds zero running projects — e.g. the compose stack hasn't been started yet, or
docker-compose isn't used on this box at all. `WizardConfigBuilder.ResolveComposeProjects` always
prefers whatever was actually discovered/checked over the fallback fields when both are present.

Per-service exclusions (`containerOverrides[].excludeFromPeriodicComposeRestart`) are still
configured by hand-editing `/etc/healer/healer.json` afterward (see `docs/README.md`'s config field
reference) — a step to auto-populate that from the discovered service list wasn't built, since
excluding a specific service is a less common, more advanced choice than "which projects to refresh
at all," which the wizard now handles with zero typing in the common case.

**"Test Compose Restart Now"** on this same step calls the real, already-tested
`DockerComposeRestartExecutor.RestartProjectAsync` directly against whatever's currently
selected/valid, showing pass/fail inline immediately — unlike the reboot test (see above), the
wizard process survives a compose restart, so there's no need for the pending-marker/verification
machinery; it's the same immediate-feedback shape as the Telegram step's "Send Test Message".
Deliberately not written to `healer-status`'s history — the SQLite DB doesn't exist yet at this
point in the wizard (pre-Apply), and the wizard already reports the result inline on the same
screen, immediately.

## Logging

Serilog, configured in two stages in `Healer.Host/Program.cs`: a console-only bootstrap logger
covers the window before config loads (since the real logger's file-sink settings come *from*
config), then a full logger with console + a resource-aware rolling file sink (size-capped,
count-capped — the same "don't let this fill the disk on a constrained box" philosophy as the
history retention). `Healer.Status` reads the tail of that same log file directly
(`Healer.Status.Logic.LogFileReader`) — no separate log-shipping setup needed.

**Getting the full log out, not just the visible tail**: `healer-status` runs full-screen
(Terminal.Gui's alternate screen buffer), so a normal terminal mouse-drag selection can only ever
grab what's currently rendered — there's no scrollback to select past, which is a real usability gap
for a long log. Ctrl+E (`Healer.Status.Program`'s `ExportFullLogAsync`) reads the complete
underlying log file (not the 500-line tail `RefreshAsync` displays) and writes it to
`LogFileReader.BuildExportFilePath` — a timestamped file in the same log directory — then makes a
best-effort attempt at `Application.Clipboard`/`Terminal.Gui.App.Clipboard` too. The file write is
the mechanism actually relied on: Terminal.Gui's clipboard needs `xclip`/`xsel`/`wl-copy` on plain
Linux (not installed on a headless EC2 box by default) or, when run inside WSL specifically,
`powershell.exe` interop — neither is guaranteed over an arbitrary SSH session, while a plain file
can always be `cat`/`scp`'d out afterward.
