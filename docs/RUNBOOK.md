# Runbook

## First-time build/publish

1. `dotnet build Healer.slnx && dotnet test Healer.slnx` — confirm the full suite is green before
   publishing anything.
2. **AOT publish spike (do this before relying on it for anything else)**: publish `Healer.Host` for
   both RIDs **from native Linux** — a GitHub Actions Linux runner or a Linux SDK container/buildx.
   Windows cannot reliably cross-compile Native AOT for Linux.
   ```sh
   dotnet publish src/Healer.Host -c Release -r linux-x64   --self-contained true -p:PublishAot=true
   dotnet publish src/Healer.Host -c Release -r linux-arm64 --self-contained true -p:PublishAot=true
   ```
   Watch the publish output for trim warnings — particularly around `Microsoft.Data.Sqlite` and
   Serilog's file sink, both flagged as "should work, verify" rather than guaranteed-safe. Neither
   uses object destructuring or complex reflection in how Healer uses them, which is the main risk
   surface for each.
3. `dotnet publish src/Healer.Setup -c Release -r linux-x64 --self-contained true` and the same for
   `Healer.Status` and `linux-arm64` — these are NOT AOT-published (see ARCHITECTURE.md for why).
4. Package `healer` (from Host), `healer-setup`, `healer-status`, `deploy/healer.service`, and
   `deploy/healer-first-run.sh` into a release tarball per architecture (`healer-linux-x64.tar.gz`,
   `healer-linux-arm64.tar.gz`), published wherever `deploy/bootstrap.sh`'s
   `HEALER_RELEASE_BASE_URL` points. `healer-first-run.sh` must be present — `bootstrap.sh` copies
   it from the extracted install directory to `/etc/profile.d/`, and that's what makes the wizard
   trigger automatically on first interactive login (see below).

## Three ways a box gets set up

The exact same two lines (set `HEALER_RELEASE_BASE_URL` to the release you built, then pipe
`bootstrap.sh` to a shell) cover all three — it always detects which applies and does the right
thing:

```sh
export HEALER_RELEASE_BASE_URL="https://github.com/nyingimaina/healer/releases/download/<YOUR_VERSION_TAG>"
curl -fsSL https://raw.githubusercontent.com/nyingimaina/healer/main/deploy/bootstrap.sh | sudo -E sh
```

**A. Interactive — someone SSHes in and pastes the one-liner.** Stages the binaries and launches the
wizard immediately in that same session. The simplest path; needs nothing else from you. (Even
though this runs as `curl | sh` — whose own stdin is the pipe from curl, not your terminal —
`bootstrap.sh` re-attaches stdin to `/dev/tty` before handing off, so the wizard still sees a real
terminal. See `docs/ARCHITECTURE.md` if you're touching that script.)

**B. Unattended, with Telegram credentials — the path that actually protects a "launch and forget"
box.** Before pasting the one-liner into an EC2 instance's User Data field (or a launch template, or
baking it into a golden AMI), set these as environment variables in the same User Data script:

```sh
export TELEGRAM_BOT_TOKEN="123456789:AAAA...your-bot-token"
export TELEGRAM_CHAT_ID="123456789"
export HEALER_SERVER_NAME="prod-api-1"   # optional — defaults to the instance's hostname
export HEALER_RELEASE_BASE_URL="https://github.com/nyingimaina/healer/releases/download/<YOUR_VERSION_TAG>"
curl -fsSL https://raw.githubusercontent.com/nyingimaina/healer/main/deploy/bootstrap.sh | sh
```

The box configures and starts Healer itself at boot — dry-run, Balanced profile, no human ever
needs to log in. This is the **only** path that protects a box nobody ever interactively logs into:
a purely login-triggered wizard can never reach it. It's still dry-run by default, so review at
least one instance's behavior via `healer-status` before deciding to turn it live fleet-wide (edit
`/etc/healer/healer.json` on that box, or re-run `healer-setup` interactively).

**C. Unattended, with no credentials.** Same User Data path as B but without the env vars — stages
everything (binaries, symlinks, the systemd unit file, the first-login trigger) but does **not**
write `/etc/healer/healer.json` or start the service. The box sits inert and safe until a human logs
in. The first time anyone does, `/etc/profile.d/healer-first-run.sh` launches `healer-setup`
automatically — no command to remember, and if they're not already root it prints
`Run: sudo healer-setup` instead of failing silently.

Every path is idempotent to interrupt: nothing is written until setup (wizard or unattended)
actually completes, so a dropped SSH connection mid-wizard, or a User Data run with no credentials,
just means the *next* interactive login offers the wizard again.

## Rolling out to a new box

1. Set the box up via path A, B, or C above.
2. Complete the setup wizard with **dry-run left on** (the default).
3. Let it run in dry-run for **several days across a normal traffic cycle**. Check `healer-status`
   periodically: does the incident volume look sane? Does the worst-offender selection (when host
   memory is critical) pick a container that actually looks like the real problem?
4. Confirm `systemctl is-enabled healer` reports `enabled` and `systemctl is-active healer` reports
   `active` — both should already be true from the wizard's Apply step, but verify.
5. Only once you trust what you're seeing, re-run `healer-setup` → "Edit existing configuration" and
   turn dry-run off, or edit `/etc/healer/healer.json` directly (`"dryRun": false`) and
   `systemctl restart healer`.
6. Repeat per box — **never flip every box to live at once.**

## Verifying reboot/crash survival on a box

- `sudo systemctl is-enabled healer` → `enabled` (survives `systemctl daemon-reload` and reboots).
- `sudo reboot` on a staging box, then confirm `systemctl is-active healer` comes back `active`
  automatically with no manual intervention.
- Kill the process directly (`sudo pkill -9 healer`) and confirm systemd restarts it within
  `RestartSec` (5s).
- To sanity-check the watchdog path specifically: temporarily lower `WatchdogSec` in the unit file
  to something short (e.g. 10s) on a test box, then block the daemon's tick loop somehow (a debug
  build that sleeps past the watchdog interval works) and confirm systemd kills/restarts it — then
  revert `WatchdogSec` to 90 before shipping that unit file for real.

## Manual dry-run smoke test

Point `Healer.Host` at a local Docker daemon with `"dryRun": true`, deliberately push a throwaway
container into an unhealthy/OOM state (e.g. a `stress`-based container with a tight `mem_limit`),
and confirm:
- A Telegram "would have restarted" message arrives.
- No restart actually happens (`docker inspect` shows the same container, unchanged `RestartCount`).
- The incident is visible in `healer-status`'s incident browser.

Then flip `dryRun` off on that same box and confirm the actual restart happens, backoff/circuit
state gets written to `/var/lib/healer/state.json`, and repeated crash-looping eventually opens the
circuit (visible as `SkippedCircuitOpen` entries) and switches to alert-only for that container.

## History/retention check

Let the daemon accumulate snapshots, then either wait out the real retention windows or temporarily
shrink `resourceSnapshotRetentionDays`/`actionHistoryRetentionDays` in config for a faster test.
Confirm the daily retention sweep (`retentionSweepHour`) deletes rows older than each cutoff, leaves
recent rows and all `actions` rows within their own window intact, and that
`/var/lib/healer/history.db`'s file size doesn't grow unbounded over time (the incremental vacuum
should keep it roughly proportional to what's actually retained).

## When something looks wrong

1. `journalctl -u healer -f` — live daemon logs (also written to `/var/log/healer/healer-*.log`,
   viewable via `healer-status`'s Logs panel without SSH-ing in a second time).
2. `healer-status` — live health, incident history, and the memory trend sparkline in one place.
3. If Telegram alerts stop arriving but the daemon is otherwise healthy: check
   `/etc/healer/healer.env` has both `TELEGRAM_BOT_TOKEN`/`TELEGRAM_CHAT_ID` set, and re-run the
   wizard's "Send Test Message" step if unsure.
4. If the daemon itself won't start: `systemctl status healer` and check the config file is valid
   JSON — a corrupt or invalid `/etc/healer/healer.json` fails fast at startup with a logged reason
   rather than starting with wrong values.
