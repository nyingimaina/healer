#!/usr/bin/env bash
# Copies a built Healer .deb onto an Ubuntu EC2 box over scp, and optionally installs it there
# immediately over ssh. Works from Linux, macOS, or WSL. For the Windows/PowerShell equivalent, see
# Deploy-ToEc2.ps1 alongside this file.
#
# Usage:
#   deploy-to-ec2.sh [-k KEY.pem] [-i IP] [-u USER] [-f path/to/healer_x.y.z_amd64.deb] [--install|--no-install]
#
# Anything not supplied on the command line is asked for interactively. Run with no arguments at
# all for the fully guided experience.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

KEY_FILE=""
EC2_IP=""
EC2_USER="ubuntu"
DEB_FILE=""
DO_INSTALL="ask" # ask | yes | no

usage() {
    cat <<EOF
Usage: $(basename "$0") [-k KEY.pem] [-i IP] [-u USER] [-f FILE.deb] [--install|--no-install]

  -k, --key FILE      Path to your EC2 .pem private key. Prompted for if omitted.
  -i, --ip ADDRESS    EC2 instance public IP or hostname. Prompted for if omitted.
  -u, --user NAME     SSH username (default: ubuntu, matching the Ubuntu .deb this ships).
  -f, --file FILE     Path to the .deb to deploy. Defaults to the newest dist/healer_*_amd64.deb.
      --install       Install it immediately over ssh after copying, no prompt.
      --no-install    Copy only; don't ask about installing.
  -h, --help          Show this help.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
    -k | --key)
        KEY_FILE="$2"
        shift 2
        ;;
    -i | --ip)
        EC2_IP="$2"
        shift 2
        ;;
    -u | --user)
        EC2_USER="$2"
        shift 2
        ;;
    -f | --file)
        DEB_FILE="$2"
        shift 2
        ;;
    --install)
        DO_INSTALL="yes"
        shift
        ;;
    --no-install)
        DO_INSTALL="no"
        shift
        ;;
    -h | --help)
        usage
        exit 0
        ;;
    *)
        echo "Unknown argument: $1" >&2
        usage
        exit 1
        ;;
    esac
done

if [ -z "$DEB_FILE" ]; then
    # shellcheck disable=SC2012
    DEB_FILE="$(ls -t "$REPO_ROOT"/dist/healer_*_amd64.deb 2>/dev/null | head -n1 || true)"
fi
if [ -z "$DEB_FILE" ] || [ ! -f "$DEB_FILE" ]; then
    echo "Couldn't find a built .deb. Run deploy/build-deb.sh first, or pass one explicitly with -f." >&2
    exit 1
fi

if [ -z "$KEY_FILE" ]; then
    read -r -p "Path to your EC2 .pem key file: " KEY_FILE
fi
KEY_FILE="${KEY_FILE/#\~/$HOME}"
if [ ! -f "$KEY_FILE" ]; then
    echo "Key file not found: $KEY_FILE" >&2
    exit 1
fi
chmod 400 "$KEY_FILE"

if [ -z "$EC2_IP" ]; then
    read -r -p "EC2 instance public IP or hostname: " EC2_IP
fi

REMOTE_FILE_NAME="$(basename "$DEB_FILE")"
REMOTE_PATH="/tmp/$REMOTE_FILE_NAME"

echo "Copying $REMOTE_FILE_NAME to $EC2_USER@$EC2_IP:$REMOTE_PATH ..."
scp -i "$KEY_FILE" -o StrictHostKeyChecking=accept-new "$DEB_FILE" "$EC2_USER@$EC2_IP:$REMOTE_PATH"
echo "Copied."

if [ "$DO_INSTALL" = "ask" ]; then
    read -r -p "Install it now over SSH? This runs the guided setup wizard right here. [Y/n] " REPLY
    case "$REPLY" in
    [nN]*) DO_INSTALL="no" ;;
    *) DO_INSTALL="yes" ;;
    esac
fi

if [ "$DO_INSTALL" = "yes" ]; then
    echo "Connecting and installing — the setup wizard should appear below..."
    # -t forces a real pseudo-terminal even for this single remote command. Without it, ssh
    # running one command non-interactively does NOT allocate a tty, and healer-setup would
    # silently take its "no terminal, stage only" branch instead of showing the wizard — the exact
    # gotcha documented in docs/ARCHITECTURE.md's dpkg-postinst risk callout. This is what avoids it.
    ssh -t -i "$KEY_FILE" -o StrictHostKeyChecking=accept-new "$EC2_USER@$EC2_IP" "sudo dpkg -i '$REMOTE_PATH'"
else
    echo "Done. To install later: ssh -i \"$KEY_FILE\" $EC2_USER@$EC2_IP   then:   sudo dpkg -i $REMOTE_PATH"
fi
