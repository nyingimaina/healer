# Healer

A self-healing daemon for Docker containers and Linux hosts on resource-constrained EC2 instances.
It watches container health and host resource usage, and steps in automatically — restarting a
crashed container, relieving memory pressure, or rebooting on a schedule you choose — while keeping
you informed on Telegram without drowning you in routine status noise.

## What's in this repo

| Project | What it is |
|---|---|
| `Healer.Core` | All decision logic (thresholds, backoff/circuit-breaker, scheduling, retention). No I/O, fully unit-tested. |
| `Healer.Host` | The always-on daemon. Native AOT, talks to Docker/`/proc`/SQLite/Telegram/systemd directly. |
| `Healer.Setup` | The interactive installer (`healer-setup`) — a guided Terminal.Gui wizard. This IS the installer; there's no separate install script. |
| `Healer.Status` | A read-only live dashboard (`healer-status`) — current health, incident history, trend sparklines, recent logs. |
| `Healer.Tests`, `Healer.Host.Tests`, `Healer.Setup.Tests`, `Healer.Status.Tests` | xUnit test suites, one per project whose logic is worth testing in isolation. |

> **New to this and deploying from a Windows machine?** Skip straight to
> `docs/DEPLOY-FROM-WINDOWS.md` — a complete, copy-paste, no-assumed-knowledge walkthrough from "I
> have a Windows PC" to "Healer is running on an EC2 box," including the recommended way to get the
> Linux binaries built (GitHub Actions, for free) since Windows can't build them itself.

## Installing on a box

As root, on a fresh EC2 instance, once you've built a release (see `docs/RUNBOOK.md` or
`docs/DEPLOY-FROM-WINDOWS.md` for how — GitHub Actions builds it for you on a tag push):

```sh
export HEALER_RELEASE_BASE_URL="https://github.com/nyingimaina/healer/releases/download/<YOUR_VERSION_TAG>"
curl -fsSL https://raw.githubusercontent.com/nyingimaina/healer/main/deploy/bootstrap.sh | sudo -E sh
```

This downloads the right build for the box's architecture (linux-x64 or linux-arm64) and launches
the setup wizard immediately in that same session. The wizard is designed so a non-technical person
can run it: almost every question is a menu or checkbox, not a text field.

The exact same one-liner also works pasted into an EC2 instance's **User Data** field for
golden-AMI/launch-template workflows, where there's no terminal attached at boot. Two outcomes there:

- **With `TELEGRAM_BOT_TOKEN`/`TELEGRAM_CHAT_ID` set as environment variables** (in the same User
  Data script, before the curl line): Healer configures and starts itself automatically at boot —
  dry-run, Balanced profile — with no human ever needing to log in. This is the only way a box
  nobody ever logs into still ends up protected.
- **Without them**: it stages everything and the wizard launches itself automatically the first time
  anyone actually logs into the box.

See `docs/RUNBOOK.md` for the exact User Data snippet and full details.

The wizard asks for:

- **A server name** — shown on every Telegram alert. Pre-filled with the box's hostname.
- **A Telegram bot token and chat id** — the only two fields you actually have to type/paste, with
  inline format validation and a "Send Test Message" button so you know immediately if it worked.
- **A safety profile** — Conservative / Balanced / Aggressive, picked from a list.
- **Whether to start in dry-run mode** (recommended) — Healer will alert on everything it *would*
  do, without doing it, until you're confident and flip it off.
- **Optional scheduled reboots** — a handful of sensible intervals (daily/weekly/every 2 weeks/
  monthly) plus a validated custom entry, and a curated low-traffic hour list.
- **Optional scheduled compose refresh** — Healer looks for running docker-compose projects itself
  (reading the labels `docker compose up` already stamps on their containers) and shows them as a
  check-off list; nothing to type or look up. Check the ones to periodically restart (all their
  containers together, via `docker compose restart`, on the same interval/hour presets as scheduled
  reboots). If nothing is running yet, it falls back to a manual directory entry, validated against
  the actual filesystem before you can continue. A **"Test Compose Restart Now"** button on this
  same screen restarts whatever's currently selected right away and shows pass/fail immediately —
  no need to wait for the schedule to prove it works.

At the end it writes `/etc/healer/healer.json` + `/etc/healer/healer.env`, installs the systemd
unit, and starts the service — then confirms on Telegram that it's running. On the final screen, a
**"Test Reboot Now"** button (behind a confirmation, since this is a real reboot) lets you confirm
this box actually comes back up with Healer running afterward, whether or not you scheduled
automatic reboots — since a reboot kills the wizard itself, the confirmation arrives on Telegram a
short while after you reconnect, not in the wizard.

## Re-running the wizard

`healer-setup` detects an existing config and offers "Edit existing configuration" or "Start
fresh". Advanced numeric tuning (exact thresholds, retention days, poll intervals) isn't exposed in
the wizard at all — it's fully determined by the safety profile you pick. To fine-tune a specific
number, edit `/etc/healer/healer.json` directly; see the field reference below.

## Commands reference

Everything below runs **on the EC2 box itself** (over SSH), except the last section. None of these
binaries take command-line flags — every one of them is either fully interactive or driven by
environment variables, listed where relevant.

### `healer-setup` — install / reconfigure

The guided TUI wizard. This IS the installer — there's no separate install step.

```sh
sudo healer-setup
```

Needs root (it writes `/etc/healer/`, installs the systemd unit, and its preflight check verifies
it's running as root). Run it again any time to change settings — it detects the existing config and
offers **"Edit existing configuration"** or **"Start fresh"** as a menu choice, never a typed
decision.

Unattended (no interactive terminal — EC2 User Data, a golden AMI bake): set these environment
variables first, then run it. It always starts in dry-run with the Balanced profile regardless of
what's supplied — see `docs/RUNBOOK.md` for the full unattended-provisioning flow.

```sh
export TELEGRAM_BOT_TOKEN="<YOUR_TELEGRAM_BOT_TOKEN>"
export TELEGRAM_CHAT_ID="<YOUR_TELEGRAM_CHAT_ID>"
export HEALER_SERVER_NAME="<A_NAME_FOR_THIS_BOX>"   # optional — defaults to the hostname
sudo -E healer-setup
```

### `healer-status` — live dashboard

```sh
sudo healer-status
```

Shows current host/container health, an incident/action history table (scheduled host reboots,
scheduled compose restarts, crash-loop restarts, everything — newest first), a text sparkline of
host memory over the last 24h, and the tail of Healer's own log file. `sudo` is needed because it
reads the Docker socket the same way the daemon does — without it, container health just won't
populate (Docker denies the socket read) even though the rest of the screen still works.

Keys: **Ctrl+Q** to quit. **Ctrl+E** to export the *full* log file (not just what's visible on
screen) to a timestamped file next to it — a full-screen terminal app like this one can't be copied
from with a normal mouse-drag selection past whatever's currently displayed, so the exported file is
what you `cat`/`scp` out over the same SSH session.

Reads whichever config `HEALER_CONFIG_PATH` points at (default `/etc/healer/healer.json` — the same
default the daemon uses), so it always reflects the same box the daemon is actually running on.

### `healer` — the daemon itself

You never run this by hand — `healer-setup` installs it as the `healer` systemd service, which
starts it and keeps it running (`Restart=always`, survives reboots). All of the following are
copy-pasteable as-is:

```sh
# Is it running right now?
sudo systemctl is-active healer

# Full status: running/failed, uptime, recent log lines, memory use against its own MemoryMax
sudo systemctl status healer

# Will it start automatically after a reboot?
sudo systemctl is-enabled healer

# Live-tail its logs (Ctrl+C to stop watching — this does not stop the daemon)
sudo journalctl -u healer -f

# Last 50 log lines without following
sudo journalctl -u healer -n 50 --no-pager

# Restart it (e.g. after hand-editing /etc/healer/healer.json)
sudo systemctl restart healer

# Stop it / start it again
sudo systemctl stop healer
sudo systemctl start healer
```

`sudo systemctl status healer` is the fastest single answer to "is it running": look for
`Active: active (running)` near the top. If it instead says `failed` or `inactive (dead)`, the lines
right below it are the last thing the daemon logged before stopping — usually enough to tell you
why (a bad config edit is the most common cause; `journalctl -u healer -n 50` shows more).

Configuration: `HEALER_CONFIG_PATH` (default `/etc/healer/healer.json`), plus whatever Telegram
env vars `telegram.botTokenEnvVar`/`telegram.chatIdEnvVar` in that file name (by default
`TELEGRAM_BOT_TOKEN`/`TELEGRAM_CHAT_ID`, loaded from `/etc/healer/healer.env` via the systemd unit's
`EnvironmentFile=`) — none of these are meant to be set by hand day-to-day, `healer-setup` manages
both files for you.

### Deployer utilities — run from *your own* machine, not the EC2 box

`deploy/deploy-to-ec2.sh` (Linux/macOS/WSL) and `deploy/Deploy-ToEc2.ps1` (Windows PowerShell) copy a
built `.deb` to an EC2 box over `scp` and optionally install it over `ssh` — see
`docs/DEPLOY-FROM-WINDOWS.md` Option C for the full walkthrough. Run with no arguments for a fully
guided, prompted experience:

```sh
./deploy/deploy-to-ec2.sh
```

```powershell
.\deploy\Deploy-ToEc2.ps1
```

Or pass everything up front (`-h`/no args shows the same flags for the PowerShell version, named
`-Key`/`-Ip`/`-User`/`-DebFile`/`-Install`/`-NoInstall`):

```sh
./deploy/deploy-to-ec2.sh -k mykey.pem -i 1.2.3.4 -u ubuntu -f dist/healer_1.0.0-1_amd64.deb --install
```

## Config field reference

See `deploy/healer.config.sample.json` for a fully-populated example. Key sections:

- `serverName` — **required**. Prefixes every Telegram message.
- `dryRun` — when true, every mutating action is replaced with a "would have done X" notice.
- `notificationLevel` — `ProblemsOnly` (default: failures, circuit-opens, dry-run previews, and
  reboots — never routine successful remediations) or `Everything` (every incident and outcome).
- `thresholds` — host mem/swap/disk/load-average, and per-container memory thresholds that fall
  back to a host-relative percentage when a container has no `mem_limit` set.
- `restartPolicy` — backoff stages, circuit-breaker thresholds, and the global cooldown that spaces
  actions out so remediation itself doesn't degrade availability.
- `hostPressureRelief` — which pressure-relief actions (prune, swapfile, drop caches) are enabled.
- `scheduledReboots` — host and per-container reboot windows, defined as an interval in days plus a
  local hour (see `RebootSchedule` in `Healer.Core`).
- `scheduledComposeRestarts` — periodically refresh an entire docker-compose project by running
  `docker compose restart` in its working directory (compose handles dependency-aware shutdown/
  startup ordering itself, all at once — Healer doesn't stagger this one the way it staggers
  individual container restarts). Same interval/hour schedule shape as `scheduledReboots.host`; a
  container can be exempted from this refresh via `containerOverrides[].excludeFromPeriodicComposeRestart`.
- `history` — SQLite history DB path, snapshot cadence, and the two-tier retention policy (15-day
  raw snapshots, 90-day action/incident history by default).
- `logging` — Serilog rolling-file settings (directory, size/count caps).

## Building from source

```sh
dotnet build Healer.slnx
dotnet test Healer.slnx
```

Publishing requires native Linux (see `docs/RUNBOOK.md`) — `deploy/build-payload.sh` publishes all
three binaries and assembles them correctly in one step (the same script the `.deb` build and the
GitHub Actions release workflow both use, so there's exactly one place this logic lives):

```sh
./deploy/build-payload.sh linux-x64 publish/bundle
```

`Healer.Setup`/`Healer.Status` target `net10.0` (Terminal.Gui 2.4.17's minimum) and publish as
self-contained, single-file (non-AOT) apps — they're one-shot/on-demand tools, not the always-on
daemon, so they aren't under the same footprint/trim constraints.

See `docs/ARCHITECTURE.md` for the design rationale and `docs/RUNBOOK.md` for rollout guidance.
