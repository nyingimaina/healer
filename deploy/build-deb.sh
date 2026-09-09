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

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found on PATH. Install the .NET 8 and .NET 10 SDKs first (see docs/DEPLOY-FROM-WINDOWS.md's WSL section)." >&2
    exit 1
fi
if ! command -v dpkg-deb >/dev/null 2>&1; then
    echo "dpkg-deb not found — this script must run on a Debian-family Linux (Ubuntu, including WSL Ubuntu)." >&2
    exit 1
fi

echo "== Publishing linux-x64 binaries (version $VERSION) =="
rm -rf publish
dotnet publish src/Healer.Host -c Release -r linux-x64 --self-contained true -p:PublishAot=true -o publish/host
# Healer.Setup/Status are self-contained but NOT AOT (Terminal.Gui's reflection use is fine for a
# one-shot tool). Without -p:PublishSingleFile, "--self-contained true" still produces ~220 files
# (the apphost plus every runtime/dependency DLL and native library) — copying only the named
# executable, as this script does below, would silently ship a binary missing everything it needs
# to run. PublishSingleFile + IncludeNativeLibrariesForSelfExtract bundles it all into one real
# executable (confirmed by inspecting `dpkg-deb --contents` during development — the bug was caught
# before ever reaching a real box, not by luck).
dotnet publish src/Healer.Setup -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/setup
dotnet publish src/Healer.Status -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/status

echo "== Assembling package tree at $PKGROOT =="
rm -rf "$PKGROOT"
mkdir -p "$PKGROOT/DEBIAN" "$PKGROOT/opt/healer" "$PKGROOT/opt/healer/.extract" "$PKGROOT/etc/profile.d" "$PKGROOT/usr/local/bin"
chmod 755 "$PKGROOT/DEBIAN" # dpkg-deb requires exactly 0755-0775; belt-and-braces given the note above.
# 1777 (world-writable + sticky bit, same as /tmp) because healer-setup/healer-status run as many
# different users across their various call sites (root during postinst, root or a human via
# `sudo`, whatever `healer-first-run.sh` runs as) — this directory must be writable by all of them.
chmod 1777 "$PKGROOT/opt/healer/.extract"

cp publish/host/healer "$PKGROOT/opt/healer/healer"
# Native AOT only compiles MANAGED code to native — a genuinely native dependency like SQLite's C
# library does NOT get folded into the single `healer` executable, and still ships as its own .so
# file next to it (confirmed by inspecting `publish/host/` during development: dotnet publish
# produces it there unprompted). Missing this file is exactly what caused "sqlite error 14: unable
# to open database file" at runtime — the daemon started fine (the AOT executable itself is
# complete), but SQLite's native provider had nothing to dlopen the moment it was actually used.
cp publish/host/libe_sqlite3.so "$PKGROOT/opt/healer/libe_sqlite3.so"
chmod 755 "$PKGROOT/opt/healer/libe_sqlite3.so"

# healer-setup/healer-status are PublishSingleFile + IncludeNativeLibrariesForSelfExtract bundles
# (see above) — at every run, they need to extract their native libraries to a writable directory,
# and confirmed BY ACTUALLY RUNNING THE PACKAGE during development: .NET's automatic fallback
# search for that directory (HOME, TMPDIR, etc.) is not reliable across the different contexts
# these tools run in — it failed with "a read-write cache directory couldn't be created" when
# invoked in this project's real deployment paths. The fix is to pin an explicit, always-writable
# extraction directory rather than depend on that guesswork: rename the real executables to
# `.bin`, and ship a thin wrapper script under the original name that sets
# DOTNET_BUNDLE_EXTRACT_BASE_DIR before exec-ing it. Every existing call site (postinst,
# healer-first-run.sh, and the /usr/local/bin symlinks) invokes healer-setup/healer-status by
# their original name, so this fix applies everywhere with no changes needed anywhere else.
cp publish/setup/healer-setup "$PKGROOT/opt/healer/healer-setup.bin"
cp publish/status/healer-status "$PKGROOT/opt/healer/healer-status.bin"
chmod 755 "$PKGROOT/opt/healer/healer-setup.bin" "$PKGROOT/opt/healer/healer-status.bin"

for name in healer-setup healer-status; do
    cat >"$PKGROOT/opt/healer/$name" <<EOF
#!/bin/sh
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="/opt/healer/.extract"
exec "/opt/healer/$name.bin" "\$@"
EOF
done
# healer.service is shipped INSIDE /opt/healer, not at the Debian-canonical
# /lib/systemd/system/healer.service. This is deliberate: Healer.Setup.Logic.HealerInstaller
# already expects the unit file sitting next to the healer-setup binary and owns the entire
# systemd lifecycle itself (copy to /etc/systemd/system, daemon-reload, enable --now). Putting it
# here means zero application code changes were needed for this package — see docs/ARCHITECTURE.md.
cp deploy/healer.service "$PKGROOT/opt/healer/healer.service"
cp deploy/healer-first-run.sh "$PKGROOT/etc/profile.d/healer-first-run.sh"

chmod 755 "$PKGROOT/opt/healer/healer" "$PKGROOT/opt/healer/healer-setup" \
    "$PKGROOT/opt/healer/healer-status" "$PKGROOT/etc/profile.d/healer-first-run.sh"
chmod 644 "$PKGROOT/opt/healer/healer.service"

ln -sf /opt/healer/healer-setup "$PKGROOT/usr/local/bin/healer-setup"
ln -sf /opt/healer/healer-status "$PKGROOT/usr/local/bin/healer-status"

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
