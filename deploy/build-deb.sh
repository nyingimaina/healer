#!/usr/bin/env bash
# Builds a single-file Debian package (.deb) containing the Healer daemon, setup wizard, and status
# dashboard for Ubuntu 64-bit (linux-x64). Run this from inside WSL (or any Debian-family Linux) —
# it needs the .NET 8 and .NET 10 SDKs on PATH plus dpkg-deb (already part of every Ubuntu install).
# See docs/DEPLOY-FROM-WINDOWS.md for the guided walkthrough, and docs/ARCHITECTURE.md for why this
# package's postinst/prerm/postrm are shaped the way they are.
#
# Usage: deploy/build-deb.sh [VERSION]
#   VERSION defaults to 1.0.0-1 — there's no git repo (yet) to derive one from automatically. Bump
#   the Debian revision (the "-N" suffix) for a rebuild of the same source, or the version itself
#   for an actual functional change.
#
# The actual `dotnet publish` + payload assembly (shared with .github/workflows/release.yml) lives
# in deploy/build-payload.sh — this script only adds what's specific to the .deb format itself:
# DEBIAN/ control metadata, the postinst/prerm/postrm lifecycle scripts, the /usr/bin symlinks, and
# relocating healer-first-run.sh to its Debian-conventional /etc/profile.d location.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

VERSION="${1:-1.0.0-1}"
OUT_FILE="dist/healer_${VERSION}_amd64.deb"

# The package tree is assembled on WSL's OWN native filesystem (via mktemp -d, typically under
# /tmp), NOT under this repo's /mnt/d/... path. This machine's /etc/wsl.conf has the `metadata`
# DrvFs mount option misplaced under [boot] instead of [automount] (a pre-existing, harmless-looking
# "Unknown key" warning at every `wsl` invocation) — so it never actually takes effect, and /mnt/d
# has NO real Unix permission tracking at all: every file and directory there reports 777
# regardless of any `chmod`, which dpkg-deb correctly refuses to package (it requires the control
# directory to be =0755-0775). Building on native ext4 sidesteps this entirely; only the finished
# .deb file itself is written back to this repo's dist/ folder at the end, which is fine — that's
# a plain file copy, unrelated to the permission-tracking of the directory it's copied INTO.
BUILD_ROOT="$(mktemp -d)"
trap 'rm -rf "$BUILD_ROOT"' EXIT
PKGROOT="$BUILD_ROOT/healer_${VERSION}_amd64"

if ! command -v dpkg-deb >/dev/null 2>&1; then
    echo "dpkg-deb not found — this script must run on a Debian-family Linux (Ubuntu, including WSL Ubuntu)." >&2
    exit 1
fi

echo "== Assembling package tree at $PKGROOT =="
rm -rf "$PKGROOT"
mkdir -p "$PKGROOT/DEBIAN" "$PKGROOT/opt/healer" "$PKGROOT/etc/profile.d" "$PKGROOT/usr/bin"
chmod 755 "$PKGROOT/DEBIAN" # dpkg-deb requires exactly 0755-0775; belt-and-braces given the note above.

chmod +x "$SCRIPT_DIR/build-payload.sh"
"$SCRIPT_DIR/build-payload.sh" linux-x64 "$PKGROOT/opt/healer" "$VERSION"

# healer-first-run.sh moves OUT of /opt/healer to its Debian-conventional /etc/profile.d location —
# dpkg can track arbitrary install paths, unlike build-payload.sh's flat layout (shared with the
# tarball path, where everything has to stay under one root because that's all `tar -xzf` extracts
# to). healer.service deliberately STAYS inside /opt/healer for the .deb too — see
# docs/ARCHITECTURE.md for why (Healer.Setup.Logic.HealerInstaller already expects it there).
mv "$PKGROOT/opt/healer/healer-first-run.sh" "$PKGROOT/etc/profile.d/healer-first-run.sh"
chmod 755 "$PKGROOT/etc/profile.d/healer-first-run.sh"

# /usr/bin, NOT /usr/local/bin: a real install via the tarball path (deploy/bootstrap.sh, which
# used /usr/local/bin at the time) reported "command not found" for both plain and `sudo`-prefixed
# invocations despite the symlink existing, because that box's PATH/sudo secure_path didn't include
# /usr/local/bin. /usr/bin is unconditionally on every PATH and every sudo secure_path. This is also
# now the Debian-Policy-correct location (§9.1.2 reserves /usr/local for the sysadmin's own,
# non-package-managed installs) — fixing the real bug also happens to fix a pre-existing, previously
# accepted policy deviation.
ln -sf /opt/healer/healer-setup "$PKGROOT/usr/bin/healer-setup"
ln -sf /opt/healer/healer-status "$PKGROOT/usr/bin/healer-status"
ln -sf /opt/healer/healer-disable.sh "$PKGROOT/usr/bin/healer-disable"
ln -sf /opt/healer/healer-enable.sh "$PKGROOT/usr/bin/healer-enable"

echo "== Writing package metadata =="
INSTALLED_SIZE_KB=$(du -sk --exclude="$PKGROOT/DEBIAN" "$PKGROOT" 2>/dev/null | cut -f1 || echo 0)

cat >"$PKGROOT/DEBIAN/control" <<EOF
Package: healer
Version: $VERSION
Section: admin
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_SIZE_KB
Depends: systemd
Recommends: docker.io | docker-ce
Maintainer: Nyingi Maina <nyingimaina@gmail.com>
Description: Self-healing Docker/host resource guardian daemon
 Healer watches Docker containers and host resource health on resource-
 constrained boxes and takes corrective action (dry-run by default), with
 Telegram alerts and a two-tier history retained in SQLite.
 .
 Includes the healer daemon, the healer-setup interactive/unattended
 configuration wizard, and the healer-status live terminal dashboard.
EOF

# postinst is a THIN wrapper around the already-built, already-tested healer-setup binary — it
# must never reimplement config-writing or systemd-enabling logic in shell. healer-setup's own
# Console.IsInputRedirected branching (interactive TUI / unattended-via-env-vars / stage-and-exit-1)
# already handles every case correctly.
cat >"$PKGROOT/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e

case "$1" in
  configure)
    OLD_VERSION="$2"

    if [ -z "$OLD_VERSION" ]; then
      # Fresh install. healer-setup decides everything for itself:
      #  - interactive terminal            -> launches the guided TUI wizard
      #  - no terminal, TELEGRAM_* env set -> configures unattended (dry-run, Balanced)
      #  - no terminal, no env vars        -> prints guidance and exits 1; the
      #                                       /etc/profile.d trigger is the fallback
      # That third case's exit 1 is an EXPECTED outcome, not a packaging failure, so it must
      # never be allowed to fail this postinst (dpkg would mark the package half-configured).
      /opt/healer/healer-setup || \
        echo "healer: not configured yet -- run 'sudo healer-setup', or log in interactively and it will offer itself automatically."
    else
      # Upgrade over an already-configured box (or a same-version reinstall, which dpkg treats
      # the same way): don't re-run the wizard against existing config, but DO refresh the
      # systemd unit file — it's not dpkg-tracked (see postrm below), so a newer package version
      # changing it (resource limits, environment variables, etc.) would otherwise silently never
      # reach an already-configured box. Only healer-setup writes it initially; nothing else
      # keeps it in sync on upgrade without this.
      if [ -f /etc/systemd/system/healer.service ]; then
        cp -f /opt/healer/healer.service /etc/systemd/system/healer.service
        systemctl daemon-reload || true
      fi
      if systemctl is-enabled --quiet healer 2>/dev/null; then
        systemctl try-restart healer || true
      fi
    fi
    ;;
esac

exit 0
EOF

cat >"$PKGROOT/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e

case "$1" in
  remove)
    # Genuine uninstall: stop the daemon before its binary disappears.
    systemctl stop healer 2>/dev/null || true
    systemctl disable healer 2>/dev/null || true
    ;;
  upgrade|deconfigure)
    # Leave the running service alone -- postinst's try-restart (or a later healer-setup run)
    # picks up the new binary once the upgrade finishes unpacking.
    ;;
esac

exit 0
EOF

# /etc/systemd/system/healer.service is NOT a dpkg-tracked file (healer-setup writes it at
# runtime, dpkg never unpacks it) -- dpkg will never clean it up on its own, so both remove and
# purge must do it explicitly here. remove keeps config/state/logs; purge deletes everything,
# matching standard Debian convention.
cat >"$PKGROOT/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e

case "$1" in
  remove)
    rm -f /etc/systemd/system/healer.service
    systemctl daemon-reload 2>/dev/null || true
    ;;
  purge)
    rm -f /etc/systemd/system/healer.service
    systemctl daemon-reload 2>/dev/null || true
    rm -rf /etc/healer
    rm -rf /var/lib/healer
    rm -rf /var/log/healer
    ;;
  upgrade|failed-upgrade|abort-install|abort-upgrade|disappear)
    ;;
esac

exit 0
EOF

chmod 755 "$PKGROOT/DEBIAN/postinst" "$PKGROOT/DEBIAN/prerm" "$PKGROOT/DEBIAN/postrm"

echo "== Building $OUT_FILE =="
mkdir -p dist
# --root-owner-group bakes root:root ownership into the archive without needing fakeroot or a
# real-root build user — supported by dpkg-deb 1.18+ (confirmed present here: 1.21.1).
dpkg-deb --build --root-owner-group "$PKGROOT" "$OUT_FILE"

echo "== Done =="
echo "$OUT_FILE"
dpkg-deb --info "$OUT_FILE"
