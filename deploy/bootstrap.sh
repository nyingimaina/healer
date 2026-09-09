#!/usr/bin/env sh
# Entry point for setting up Healer on a box. Works three ways, all auto-detected — this one script
# is all you ever need to run or paste, regardless of which applies:
#
#   1. Interactive (someone SSHed in and pasted this):
#        export HEALER_RELEASE_BASE_URL="https://github.com/nyingimaina/healer/releases/download/<YOUR_VERSION_TAG>"
#        curl -fsSL https://raw.githubusercontent.com/nyingimaina/healer/main/deploy/bootstrap.sh | sudo -E sh
#      -> stages the binaries AND launches the guided setup wizard immediately in this session.
#
#   2. Non-interactive with Telegram credentials supplied (EC2 "User data", a launch template, a
#      golden AMI bake, with TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID — and optionally
#      HEALER_SERVER_NAME — set as environment variables before this script runs):
#      -> configures and starts Healer itself, unattended, always in dry-run with the Balanced
#         profile. This is the ONLY way a box nobody ever logs into still ends up protected from
#         boot — a purely login-triggered wizard can never reach that box.
#
#   3. Non-interactive with no usable credentials (User Data with nothing set):
#      -> stages the binaries and a first-login trigger, then stops. Inert and safe until a human
#         logs in, at which point the wizard launches itself automatically — see healer-first-run.sh.
#
# In every case, staging never writes /etc/healer/healer.json or starts the systemd service on its
# own — only actually completing setup (wizard or unattended) does that — so an interrupted run
# (dropped SSH session, User Data with no credentials) always leaves the box inert and safe rather
# than half-configured.
#
# NOTE: HEALER_RELEASE_BASE_URL below is a placeholder. Point it at wherever your built release
# tarballs are published (e.g. a GitHub Releases URL) before distributing this script.

set -e

HEALER_RELEASE_BASE_URL="${HEALER_RELEASE_BASE_URL:-https://example.invalid/healer/releases/latest}"
INSTALL_DIR="/opt/healer"

if [ "$(id -u)" -ne 0 ]; then
    echo "Healer needs to run as root (it manages Docker, swap, and system reboots)." >&2
    echo "Please re-run as root, e.g.: curl -fsSL <url> | sudo sh" >&2
    exit 1
fi

ARCH="$(uname -m)"
case "$ARCH" in
    x86_64)  RID="linux-x64" ;;
    aarch64) RID="linux-arm64" ;;
    *)
        echo "Unsupported architecture: $ARCH (Healer ships linux-x64 and linux-arm64 only)." >&2
        exit 1
        ;;
esac

echo "Detected architecture: $ARCH -> $RID"
echo "Downloading Healer ($RID)..."

mkdir -p "$INSTALL_DIR"
curl -fsSL "$HEALER_RELEASE_BASE_URL/healer-$RID.tar.gz" -o /tmp/healer-release.tar.gz
tar -xzf /tmp/healer-release.tar.gz -C "$INSTALL_DIR"
rm -f /tmp/healer-release.tar.gz
chmod +x "$INSTALL_DIR/healer" "$INSTALL_DIR/healer-setup" "$INSTALL_DIR/healer-status" 2>/dev/null || true

# Convenience: `healer-setup`/`healer-status` from anywhere, not the full /opt/healer/ path.
ln -sf "$INSTALL_DIR/healer-setup" /usr/local/bin/healer-setup
ln -sf "$INSTALL_DIR/healer-status" /usr/local/bin/healer-status

# The first-login trigger: fires on every interactive login until Healer is configured, so a box
# that finishes this script non-interactively (User Data) WITHOUT usable Telegram env vars still
# gets configured the moment a human actually logs in. See healer-first-run.sh for the logic.
cp "$INSTALL_DIR/healer-first-run.sh" /etc/profile.d/healer-first-run.sh
chmod +x /etc/profile.d/healer-first-run.sh

# healer-setup decides for itself what to do, so it's always run rather than branched around here:
#   - interactive terminal              -> launches the guided TUI wizard.
#   - no terminal, TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID env vars set (e.g. in EC2 User Data)
#                                        -> configures and starts itself unattended (dry-run,
#                                           Balanced profile) — the only way a box nobody ever logs
#                                           into still ends up protected from boot.
#   - no terminal, no usable env vars   -> prints guidance and exits; healer-first-run.sh (above)
#                                           is the fallback once someone does log in.
#
# Gotcha this guards against: when this script is run as `curl -fsSL <url> | sh`, its own stdin
# (fd 0) is the pipe FROM curl, not the caller's terminal — a well-known trap with piped installers.
# Left alone, healer-setup would see a non-interactive stdin and skip the wizard even for someone
# running the documented one-liner right now at their keyboard. /dev/tty always refers to the
# process's controlling terminal regardless of what fd 0 is redirected to, so we re-attach stdin to
# it when one exists (real SSH session) and fall through unchanged when it doesn't (no controlling
# terminal at all — genuinely non-interactive, e.g. EC2 User Data). The subshell isolates a failed
# /dev/tty open so it can't abort this script.
if [ -c /dev/tty ] && ( : < /dev/tty ) 2>/dev/null; then
    exec "$INSTALL_DIR/healer-setup" < /dev/tty
else
    exec "$INSTALL_DIR/healer-setup"
fi
