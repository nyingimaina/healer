# Deploying Healer to EC2, from a Windows machine — the idiot-proof version

This is a step-by-step, copy-paste guide for getting Healer running on a real Amazon EC2 server,
starting from nothing but a Windows PC. No assumed knowledge beyond "I can open a web browser and a
terminal." Every command is exact — you paste it as-is, except for the placeholder pieces explained
right below.

## The placeholder convention (read this first)

Anywhere you see text wrapped in angle brackets, like `<THIS>`, it means **"replace this whole thing,
brackets included, with your own value."** For example:

```
ssh -i <YOUR_KEY_FILE>.pem ec2-user@<YOUR_EC2_PUBLIC_IP>
```

If your key file is `my-key.pem` and your server's address is `54.12.34.56`, you'd actually type:

```
ssh -i my-key.pem ec2-user@54.12.34.56
```

Every placeholder used in this guide, in one place for reference:

This project's own GitHub repository is already `https://github.com/nyingimaina/healer` — commands
below use that directly rather than a placeholder, since it's fixed. (If you've forked this repo
under a different account/name, substitute your own `nyingimaina`/`healer` throughout.)

| Placeholder | What it means |
|---|---|
| `<YOUR_VERSION_TAG>` | A version label you make up, e.g. `v1.0.0` |
| `<YOUR_EC2_PUBLIC_IP>` | The public IP address AWS assigns your server (shown in the EC2 console) |
| `<YOUR_KEY_FILE>` | The name of the `.pem` file you download when creating a key pair |
| `<YOUR_TELEGRAM_BOT_TOKEN>` | The token @BotFather gives you when you make a Telegram bot |
| `<YOUR_TELEGRAM_CHAT_ID>` | Your Telegram chat id (a number) |
| `<YOUR_SERVER_NICKNAME>` | A short name for this box, e.g. `prod-api-1` |

Nothing else in any command needs changing — copy those parts exactly.

## Which path should I take?

There are three ways to get the Linux program files that run on the server. **Option A is strongly
recommended** — it's less work and you only set it up once, ever, even if you deploy to many boxes
later.

- **Option A (recommended): Let GitHub build it for you, for free, and host the download.** Healer's
  server component needs to be built *on Linux* — a technical detail of how it's compiled — but your
  PC is Windows. GitHub's free tier includes Linux computers that can build it for you automatically
  every time you ask, and it hosts the finished download for you too, at a stable web address your
  EC2 server can fetch from. You do this once (maybe 10 minutes of clicking), and forever after,
  setting up a new server is just one copy-pasted line.
- **Option B: Build it yourself on your own PC using WSL (Windows's built-in Linux), then copy the
  files to the server directly.** No GitHub account needed, but more manual steps, and you repeat
  the build step yourself every time something changes. Covered near the end of this guide.
- **Option C: Build a single Ubuntu package file (`.deb`) via WSL, and use the included deployer
  script to copy + install it in one go.** Also no GitHub needed, no live download on the server at
  all, and — because it's Ubuntu's real package format — updating and uninstalling later are as
  simple as `apt`/`dpkg` commands instead of manual file/systemd juggling. The best pick if your
  server is specifically Ubuntu (this guide otherwise supports both Amazon Linux and Ubuntu) and you
  want the simplest possible "one file, one command" experience.

Start with Option A unless you have a specific reason not to (e.g. you can't use GitHub at all).

> **A note on terminals: PowerShell vs. WSL.** This guide's commands are written for PowerShell,
> which every Windows 10/11 PC already has. If you have **WSL** (Windows Subsystem for Linux — a
> real Linux environment that runs inside Windows) enabled, you can use it instead for *any* step
> that isn't itself PowerShell-specific (the `git`, `ssh`, `scp`, and `curl` commands are identical
> either way) — and it's genuinely a bit smoother for connecting to your server, since it skips the
> `icacls` permission dance PowerShell needs and just uses a plain `chmod`. See **Part 4's "Option
> 3: WSL"** below for connecting that way, and **Option B** further down if you also want to *build*
> Healer yourself from WSL instead of using GitHub. Don't have WSL and don't want it? Ignore all of
> this — PowerShell alone gets you through the whole guide.

---

# Option A: GitHub builds it, EC2 downloads it

## Part 1 — Put the code on GitHub (one-time)

This project already lives at `https://github.com/nyingimaina/healer` — skip to step 4 if it's
already created and you just need to push. Otherwise, to set it up fresh under your own account:

1. Go to **https://github.com** in your browser and sign in (or create a free account if you don't
   have one).
2. Click the **+** in the top-right corner → **New repository**.
3. Name it whatever you like (e.g. `healer`) and click **Create repository**. Public is
   simplest for what follows (nothing secret lives in this code — your Telegram token is never
   stored in it); if you'd rather keep it private, see the note at the end of Part 2.
4. GitHub will show you a page with some commands. Ignore those — instead, open **PowerShell** on
   your Windows machine (Start menu → type `PowerShell` → Enter), navigate to the Healer folder, and
   run these one at a time:

   ```powershell
   cd D:\work\nyingi\code\systems\healer
   git init
   git add .
   git commit -m "Initial commit"
   git branch -M main
   git remote add origin https://github.com/nyingimaina/healer.git
   git push -u origin main
   ```

   If a browser window pops up asking you to sign in to GitHub, do that — it's just confirming it's
   really you.

   **You'll know it worked when:** refreshing the GitHub page in your browser shows all the project
   files instead of the empty "quick setup" page.

## Part 2 — Ask GitHub to build the server program (one-time setup, repeatable forever after)

This repository already includes a recipe file (`.github/workflows/release.yml`) that tells GitHub
exactly how to build Healer. You don't need to write or understand it — it's already there from
Part 1's push. You just need to trigger it by creating a **version tag**:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

(`v1.0.0` here is a `<YOUR_VERSION_TAG>` — you can call it anything starting with `v`, like `v1.0.0`,
`v1.0.1` next time, etc. Just remember whichever one you used.)

**What happens next, automatically:** GitHub spins up Linux computers, builds Healer for both common
server chip types, packages everything up, and publishes it as a "Release" — a permanent, public
download link. This takes about 3–5 minutes.

**How to watch it / know it worked:**
1. On your repository's GitHub page, click the **Actions** tab near the top.
2. You'll see a run called "Release" — click it. Green checkmarks = working; a red X means something
   went wrong (see Troubleshooting at the bottom).
3. Once it's green, click your repository's **Releases** link (usually on the right-hand sidebar of
   the main repo page, or go to `https://github.com/nyingimaina/healer/releases`).
   You should see `v1.0.0` listed with two files attached: `healer-linux-x64.tar.gz` and
   `healer-linux-arm64.tar.gz`.

**Write down this web address — you'll paste it into your server in Part 5:**

```
https://github.com/nyingimaina/healer/releases/download/<YOUR_VERSION_TAG>
```

(With `v1.0.0` as your version tag, that's
`https://github.com/nyingimaina/healer/releases/download/v1.0.0` exactly.)

> **If you made your repository private**: the plain `curl` command used later in Part 6 can't
> download from a private repo's releases without proving who it is. Either switch the repository
> to public (Settings → General → Danger Zone → Change visibility — safe here, since no secrets
> live in this code), or generate a GitHub personal access token (Settings → Developer settings →
> Personal access tokens → generate one with just `repo` read access) and add
> `-H "Authorization: token <YOUR_GITHUB_TOKEN>"` to the `curl` command in Part 6. Public is
> simpler and is what the rest of this guide assumes.

## Part 3 — Launch an EC2 server

1. Go to **https://console.aws.amazon.com/ec2** and sign in to your AWS account.
2. Make sure the region shown in the top-right (e.g. "N. Virginia") is one close to you — it doesn't
   have to match anything else in this guide.
3. Click the orange **Launch instance** button.
4. **Name**: type anything, e.g. `<YOUR_SERVER_NICKNAME>`.
5. **Application and OS Images (AMI)**: leave it on the default, **Amazon Linux** (it's usually
   already selected and marked "Free tier eligible").
6. **Instance type**: pick `t3.micro` (or, if you want the cheaper option this whole project is
   built around, `t4g.micro` — a different chip type, works identically, Healer supports both). Both
   are usually free-tier eligible for a new AWS account.
7. **Key pair (login)**: click **Create new key pair**. Give it a name (this becomes your
   `<YOUR_KEY_FILE>` name), leave the defaults (RSA, `.pem`), and click **Create key pair** — this
   downloads a `.pem` file to your Downloads folder. **This file is your only way to log in — don't
   lose it, and don't share it.**
8. **Network settings**: click **Edit**. Make sure there's a rule allowing **SSH** on port 22 from
   "My IP" (this is usually already there by default) — this is what lets you connect from your
   Windows PC.
9. Leave everything else as default, and click **Launch instance**.
10. Click **View all instances**, and wait (refresh the page) until the **Instance state** column
    shows **Running** and **Status check** shows **2/2 checks passed** — usually 1-2 minutes.
11. Click on your instance, and note the **Public IPv4 address** shown in the details panel — this
    is your `<YOUR_EC2_PUBLIC_IP>` for every step below.

## Part 4 — Connect to your server from Windows

Three ways — pick whichever feels easiest. All are free and need nothing extra installed (WSL
aside, if you don't already have it — see the callout above).

### Option 1: Straight from your browser (easiest — no software needed at all)

1. In the EC2 console, select your instance, click **Connect** (top of the page).
2. Leave it on the **EC2 Instance Connect** tab, click the orange **Connect** button.
3. A terminal opens right there in your browser, already logged in. Skip to Part 5.

### Option 2: PowerShell (built into Windows 10/11)

1. Move the `.pem` file you downloaded somewhere easy to find, e.g. `C:\Users\<your name>\Downloads`.
2. Open **PowerShell** and run (this restricts the file's permissions — AWS requires it, or it'll
   refuse to use the key):

   ```powershell
   icacls "<YOUR_KEY_FILE>.pem" /inheritance:r
   icacls "<YOUR_KEY_FILE>.pem" /grant:r "$($env:USERNAME):(R)"
   ```

3. Connect:

   ```powershell
   ssh -i <YOUR_KEY_FILE>.pem ec2-user@<YOUR_EC2_PUBLIC_IP>
   ```

4. The first time, it'll ask "Are you sure you want to continue connecting?" — type `yes` and press
   Enter.

### Option 3: WSL (if you have it enabled — see the callout above)

Simpler than PowerShell here, since Linux-style file permissions are just one plain command:

1. Open your WSL/Ubuntu window. Your Windows `D:\` drive (adjust if yours is different) is visible
   at `/mnt/d/`, so if you downloaded the `.pem` file to Windows, find it there — or just re-download
   it from inside WSL to keep everything in one place.
2. Lock down the key file's permissions (WSL/Linux refuses to use an overly-open key, same as AWS
   requires):
   ```sh
   chmod 400 <YOUR_KEY_FILE>.pem
   ```
3. Connect:
   ```sh
   ssh -i <YOUR_KEY_FILE>.pem ec2-user@<YOUR_EC2_PUBLIC_IP>
   ```
4. First time, type `yes` when asked about continuing to connect.

**Whichever option you used, you'll know it worked when** your prompt changes to something like
`[ec2-user@ip-... ~]$` — you're now typing commands on the server, not your PC.

## Part 5 — Install Docker (Healer manages Docker containers, so the box needs Docker first)

Paste this on the server (works for both Amazon Linux and Ubuntu):

```sh
curl -fsSL https://get.docker.com | sudo sh
sudo systemctl enable --now docker
```

**You'll know it worked when** this prints no errors and returns you to the prompt. You can
double-check with `sudo docker ps` — it should print an empty table header, not an error.

If you don't already have your own application containers running on this box, now's the time to
start them (`docker run ...` / `docker compose up -d` for whatever you're deploying) — Healer
watches and heals *existing* containers, it doesn't create your application for you.

## Part 6 — Install and configure Healer

Paste these two lines, replacing the first with the release address you wrote down at the end of
Part 2:

```sh
export HEALER_RELEASE_BASE_URL="https://github.com/nyingimaina/healer/releases/download/<YOUR_VERSION_TAG>"
curl -fsSL https://raw.githubusercontent.com/nyingimaina/healer/main/deploy/bootstrap.sh | sudo -E sh
```

(The first line tells the installer where to download Healer itself from — the release you built in
Part 2. The second line fetches the small installer script straight from your own repository on
GitHub and runs it. `-E` on `sudo` is what carries that first line's setting through to the
installer running as root.)

A guided wizard will appear right there in your terminal. It's almost entirely arrow-keys, Enter,
and checkboxes — the only things you'll actually type are:

1. **A server name** (already filled in with the box's hostname — keep it or change it).
2. **A Telegram bot token and chat id** — this is the only fiddly part:
   - Open Telegram, message **@BotFather**, send `/newbot`, follow its prompts, and it'll give you a
     token that looks like `123456789:AAAA...` — this is your `<YOUR_TELEGRAM_BOT_TOKEN>`.
   - Message **@userinfobot** on Telegram, and it'll reply with your numeric id — this is your
     `<YOUR_TELEGRAM_CHAT_ID>`.
   - Paste both into the wizard, then click **Send Test Message** — you should get a message on
     Telegram within a few seconds confirming it worked.

Everything else (safety level, dry-run, scheduled reboots, scheduled compose refresh) is a menu —
just press Enter to accept the recommended option unless you know you want something else. For the
compose refresh step specifically, if you already have `docker compose up` running on this box, the
wizard finds your project(s) on its own and just asks which ones (if any) to restart periodically —
you never type a folder path. At the end, it writes everything, starts Healer, and sends you one
final confirmation message on Telegram.

**You'll know it worked when** you get that final Telegram message saying Healer is installed and
running.

## Part 7 — Check on it any time

From the same SSH session (or reconnect any time using Part 4), the fastest yes/no answer to "is
the daemon actually running":

```sh
sudo systemctl is-active healer
```

This prints `active` when it's running. For more detail (uptime, memory use, its last few log
lines):

```sh
sudo systemctl status healer
```

For the full interactive dashboard:

```sh
sudo healer-status
```

(`sudo` matters here — without it, container health won't populate since it can't read the Docker
socket, even though the rest of the screen still works.) This shows live health, a history of
anything Healer has done — including scheduled host reboots and scheduled compose restarts, newest
first — and its recent logs, all in one screen. Press Ctrl+Q to exit back to the terminal, or Ctrl+E
to export the *full* log file (not just what's visible on screen) to a timestamped file you can
`cat`/`scp` off the box — a full-screen terminal app like this one can't be copied from with a
normal mouse-drag selection past whatever's currently displayed.

See `docs/README.md`'s **Commands reference** for the complete list of commands (including
`journalctl`/restart/enable checks) with copy-pasteable examples.

---

# Option B: Build it yourself with WSL (no GitHub needed)

Use this if you'd rather not use GitHub at all. More manual, and you repeat these steps yourself
every time you want an updated build.

1. **Enable WSL** (one-time; needs a restart): open PowerShell **as Administrator** and run:
   ```powershell
   wsl --install
   ```
   Restart your PC when it asks. This installs Ubuntu Linux, running inside Windows.

2. **Open Ubuntu** (search for "Ubuntu" in the Start menu after restarting), set a username/password
   when prompted, then install the two .NET versions Healer needs:
   ```sh
   sudo apt update
   wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
   chmod +x dotnet-install.sh
   ./dotnet-install.sh --channel 8.0
   ./dotnet-install.sh --channel 10.0
   export PATH="$HOME/.dotnet:$PATH"
   ```

3. **Build Healer** (still inside the Ubuntu/WSL window; adjust the path if your Windows drive isn't
   `D:`, WSL sees it as `/mnt/d/...`):
   ```sh
   cd /mnt/d/work/nyingi/code/systems/healer
   ./deploy/build-payload.sh linux-x64 publish/bundle
   ```
   This is the exact same script `deploy/build-deb.sh` (Option C) and the GitHub Actions release
   workflow (Option A) both call — it publishes all three binaries and assembles them, correctly,
   into `publish/bundle/`. (Publishing Setup/Status as a genuine single-file executable each rather
   than ~200 loose files, shipping the daemon's native SQLite dependency alongside it, and pinning
   where the single-file binaries extract to at runtime are all load-bearing, non-obvious steps —
   see `docs/ARCHITECTURE.md` if you're curious why; the point of one shared script is that you don't
   need to know any of that to get a correct build.)
   This works because WSL runs a genuine Linux kernel (not a translation layer), and since almost
   every Windows PC is an x64/amd64 machine, that's an x64 Linux kernel — so `linux-x64` here is a
   same-architecture native build, not a cross-compile, exactly like building it on a real Ubuntu box.
   > **Need `linux-arm64` instead** (a `t4g` EC2 instance)? Simply swapping `linux-x64` for
   > `linux-arm64` above would ask WSL to cross-compile from x64 to a different CPU architecture —
   > the same class of "not officially supported, may or may not work" territory as Windows→Linux
   > itself, just one step removed rather than solved by WSL. Don't rely on it. Two options that
   > actually work: **(a)** use an x64 instance type (e.g. `t3.micro`) instead of `t4g` — simplest,
   > and still cheap; or **(b)** use Option A instead for the arm64 build specifically — its GitHub
   > Actions workflow builds arm64 on a *native* arm64 Linux runner, so there's no cross-compilation
   > involved there at all.

4. **Copy the files to your EC2 server** — stay right here in WSL (after completing Parts 3–5 above:
   launch the instance, install Docker). This is the one part where WSL is genuinely easier than
   PowerShell, since there's no `icacls` step — just `chmod` on the key, same as Part 4's WSL option:
   ```sh
   chmod 400 <YOUR_KEY_FILE>.pem
   scp -i <YOUR_KEY_FILE>.pem -r publish/bundle ec2-user@<YOUR_EC2_PUBLIC_IP>:/tmp/healer-bundle
   ```
   (Prefer PowerShell instead? This works there too, once you've done the `icacls` steps from Part
   4's Option 2: `scp -i <YOUR_KEY_FILE>.pem -r D:\work\nyingi\code\systems\healer\publish\bundle ec2-user@<YOUR_EC2_PUBLIC_IP>:/tmp/healer-bundle`.)

5. **Install it on the server** — connect (Part 4 — from this same WSL window, `ssh` works exactly
   as it did to run `scp` above), then:
   ```sh
   sudo mkdir -p /opt/healer /opt/healer/.extract
   sudo chmod 1777 /opt/healer/.extract
   sudo cp /tmp/healer-bundle/* /opt/healer/
   sudo chmod +x /opt/healer/healer /opt/healer/libe_sqlite3.so /opt/healer/healer-setup /opt/healer/healer-status /opt/healer/healer-setup.bin /opt/healer/healer-status.bin
   sudo ln -sf /opt/healer/healer-setup /usr/bin/healer-setup
   sudo ln -sf /opt/healer/healer-status /usr/bin/healer-status
   sudo cp /opt/healer/healer-first-run.sh /etc/profile.d/healer-first-run.sh
   sudo chmod +x /etc/profile.d/healer-first-run.sh
   sudo healer-setup
   ```
   (`/usr/bin`, not `/usr/local/bin` — confirmed by a real install where `/usr/local/bin` wasn't on
   every box's `PATH`/`sudo` `secure_path`; `/usr/bin` always is.)

Follow the same wizard steps as Part 6 above from here on.

---

# Option C: One single file, no network needed on the server at all

Options A and B both end with the server downloading something over the internet (a GitHub
release, or nothing if you `scp`'d it — actually B already avoids this). This third option goes
further: it builds Healer into **one native Ubuntu package file** (a `.deb`, the same format
`apt install` uses under the hood) that you copy over yourself and run with a single command — no
hosting, no GitHub, and Ubuntu's own package manager handles install/upgrade/uninstall for you.

Use this if you want the simplest possible "one file, one command" experience and don't mind that
rebuilding it later is a manual step (same tradeoff as Option B).

## Step 1 — Build the `.deb` (uses WSL, same as Option B)

If you haven't already, enable WSL and install the two .NET SDKs — see Option B's steps 1–2 above
(you can skip step 3 there, the build script below does that itself). Then, inside your WSL/Ubuntu
window:

```sh
cd /mnt/d/work/nyingi/code/systems/healer
export PATH="$HOME/.dotnet:$PATH"
./deploy/build-deb.sh
```

This publishes all three binaries, assembles the package, and produces
`dist/healer_1.0.0-1_amd64.deb`. (Pass a different version as an argument, e.g.
`./deploy/build-deb.sh 1.0.1-1`, if you rebuild later.)

> Building `.deb` files is genuinely Debian/Ubuntu-specific tooling (`dpkg-deb`), which is exactly
> why this only works for Ubuntu targets — matching what this whole option is for. As with Option
> B, this only builds for `linux-x64` (Intel/AMD-based EC2 instances like `t3.micro`) — WSL's own
> kernel is x64, so that's a same-architecture native build; `linux-arm64` (a `t4g` instance) would
> require actual cross-compilation from here, which isn't reliable. Use a `t3`-family instance type
> with this option, or use Option A instead if you specifically want `t4g`/arm64.

## Step 2 — Copy it to your EC2 server and install it, with the deployer helper

After completing Parts 3–5 above (launch the instance, install Docker), use the included deployer
script instead of typing out `scp`/`ssh` commands by hand — it asks for what it needs and does both
steps for you. Pick the version matching your terminal:

**PowerShell** (Windows):
```powershell
.\deploy\Deploy-ToEc2.ps1
```

**WSL / any Linux / macOS**:
```sh
./deploy/deploy-to-ec2.sh
```

Either one prompts for your `.pem` key file path and your server's IP if you don't pass them as
arguments (`-KeyFile`/`-Ip` in PowerShell, `-k`/`-i` in the shell version — run with `-h`/`--help` or
`Get-Help .\deploy\Deploy-ToEc2.ps1` for the full list), automatically finds the newest `.deb` in
`dist\`, copies it over, and then asks: **"Install it now over SSH?"** — say yes, and the guided
setup wizard appears right there in your terminal, exactly as in Part 6, driven by one command
(`sudo dpkg -i ...`) run for you over SSH.

**You'll know it worked when** you see the wizard appear (or, if you said no to auto-install, when
the script prints the exact `ssh`/`dpkg -i` command to run yourself later) — from there, follow the
same wizard steps as Part 6.

## Updating or removing later

Because this uses Ubuntu's real package manager, updating and uninstalling are simpler than Options
A/B — no manual file copying or systemd commands needed:

- **Update**: rebuild with a new version (`./deploy/build-deb.sh 1.0.1-1`), deploy it the same way
  (`Deploy-ToEc2.ps1`/`deploy-to-ec2.sh`), and `sudo dpkg -i` it on the server — your existing
  configuration is left untouched, and the service restarts automatically with the new binary.
- **Remove, keeping your configuration and history**: `sudo apt remove healer` on the server.
- **Remove everything, including configuration and history**: `sudo apt purge healer` on the server.

---

# Doing this for a "launch and forget" fleet of boxes (advanced, optional)

If you're launching many boxes and don't want to manually run the wizard on each one, you can make
new EC2 instances configure themselves completely automatically at boot — genuinely zero login ever
required. In the EC2 launch wizard, expand **Advanced details** at the bottom → **User data**, and
paste (using Option A's release URL from Part 2):

```sh
#!/bin/sh
export TELEGRAM_BOT_TOKEN="<YOUR_TELEGRAM_BOT_TOKEN>"
export TELEGRAM_CHAT_ID="<YOUR_TELEGRAM_CHAT_ID>"
export HEALER_SERVER_NAME="<YOUR_SERVER_NICKNAME>"
export HEALER_RELEASE_BASE_URL="https://github.com/nyingimaina/healer/releases/download/<YOUR_VERSION_TAG>"
curl -fsSL https://raw.githubusercontent.com/nyingimaina/healer/main/deploy/bootstrap.sh | sh
```

Every instance launched with this User Data script configures and starts Healer itself at boot —
always in dry-run with the Balanced profile, so review its behavior via `healer-status` before
deciding to flip any given box live. See `docs/RUNBOOK.md` for the full explanation of why this only
works safely because it always defaults to dry-run.

---

# Updating Healer later

**Option A**: bump the version and push a new tag —
```powershell
git tag v1.0.1
git push origin v1.0.1
```
— then on the server, re-run the Part 6 install command with `v1.0.1` in place of `v1.0.0`.

**Option B**: repeat the WSL build + `scp` steps, then on the server run
`sudo systemctl restart healer` after copying the new `healer` binary into `/opt/healer/`.

**Option C**: `./deploy/build-deb.sh 1.0.1-1`, deploy the new file with `Deploy-ToEc2.ps1` /
`deploy-to-ec2.sh`, then `sudo dpkg -i` it on the server — see "Updating or removing later" at the
end of Option C above for the full picture, including uninstall.

# Troubleshooting

- **"Permission denied (publickey)" when SSHing**: your key file's permissions aren't locked down
  enough (re-run the `icacls` commands in Part 4), or you typed the wrong username (`ec2-user` for
  Amazon Linux, `ubuntu` for Ubuntu AMIs).
- **GitHub Actions run shows a red X**: click into it to see which step failed — the most common
  cause is a typo if you edited the workflow file; otherwise re-run it (there's a "Re-run all jobs"
  button) since it's occasionally a transient network hiccup on GitHub's side.
- **`curl` on the server returns a 404 / "Not Found"**: double-check the release URL — visit
  `https://github.com/nyingimaina/healer/releases` in your browser and confirm
  the tag name and that both `.tar.gz` files are actually listed there.
- **The wizard's preflight check says "Docker doesn't appear to be running"**: re-run Part 5, then
  `sudo systemctl status docker` to confirm it's active.
- **No Telegram message arrives after "Send Test Message"**: double check you copied the whole bot
  token (it has a colon in the middle — easy to accidentally clip), and that the chat id is the
  number @userinfobot gave you, not your @username.
- **The wizard's compose-refresh step says "No running docker-compose projects found"**: it only
  discovers projects that are already running (`docker compose up -d`) at the moment you reach that
  step — start your containers first, then press "Re-check for running projects" on the same screen,
  or just type the project directory manually in the fallback fields shown below the list.
- **Instance launched but you can't reach it at all**: check the security group (Part 3, step 8)
  actually allows SSH from your current IP — if your home IP changed since launch, edit the security
  group's inbound rule in the EC2 console to allow your new IP.
- **(Option C) The deployer script can't find a `.deb` file**: make sure `deploy/build-deb.sh`
  actually finished successfully first — it prints the output path (`dist/healer_..._amd64.deb`) at
  the end. Or pass the file explicitly with `-DebFile`/`-f`.
- **(Option C) You ran `sudo dpkg -i ...` yourself over a one-shot remote SSH command and the wizard
  never appeared**: this happens specifically when a command is run as `ssh host "sudo dpkg -i ..."`
  in one shot rather than at an interactive shell — that form doesn't allocate a real terminal, so
  Healer correctly (if unhelpfully, in this case) assumes nobody's there to answer questions and
  stages itself for later instead. Either use the deployer scripts (they already pass `ssh -t` to
  avoid this), or connect first (Part 4) and run `sudo dpkg -i` from inside that session.
- **(Option C) `dpkg -i` fails partway through**: check `sudo systemctl status healer` and
  `journalctl -u healer -n 50` for what postinst's `healer-setup` call actually did; you can safely
  re-run `sudo dpkg -i` on the same file, or `sudo healer-setup` directly, once you've fixed whatever
  it flagged (a `dpkg` install that only partially configured is not a broken package — dpkg tracks
  this and lets you retry safely).
