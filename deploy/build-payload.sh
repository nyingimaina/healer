#!/usr/bin/env bash
# Publishes Healer's three binaries for a given RID and assembles them, plus the systemd unit and
# first-login trigger script, into a single flat "payload" directory — the exact tree that ends up
# living at /opt/healer on a real box, however it gets there. This is the ONE place this logic
# lives: deploy/build-deb.sh (a .deb package) and .github/workflows/release.yml (a .tar.gz consumed
# by bootstrap.sh) both call this rather than each separately duplicating the dotnet publish flags
# and payload-assembly steps.
#
# That duplication was real, not hypothetical: the same three bugs (missing PublishSingleFile,
# missing libe_sqlite3.so, missing the DOTNET_BUNDLE_EXTRACT_BASE_DIR wrapper-script pattern) shipped
# independently in build-deb.sh, then in release.yml, then AGAIN in docs/DEPLOY-FROM-WINDOWS.md's
# manual copy-paste instructions — three hand-maintained copies of the same logic, each fixed only
# after someone actually ran the resulting install and hit the failure for real. See
# docs/ARCHITECTURE.md for the full history. All three now call this script instead.
#
# Usage: deploy/build-payload.sh <RID> <OUTPUT_DIR>
#   RID          linux-x64 or linux-arm64. Native AOT must be published FROM that architecture — see
#                docs/RUNBOOK.md/docs/DEPLOY-FROM-WINDOWS.md for why (no reliable cross-compilation).
#   OUTPUT_DIR   Where the assembled payload directory tree ends up. Created if it doesn't exist.
#                Its existing contents are NOT cleared first — the caller owns this directory and
#                may place other things alongside the payload (e.g. build-deb.sh's DEBIAN/ dir is a
#                sibling, not inside it).

set -euo pipefail

if [ $# -ne 2 ]; then
    echo "Usage: $0 <RID> <OUTPUT_DIR>" >&2
    echo "  RID: linux-x64 or linux-arm64" >&2
    exit 1
fi

RID="$1"
OUTPUT_DIR="$2"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found on PATH. Install the .NET 8 and .NET 10 SDKs first (see docs/DEPLOY-FROM-WINDOWS.md's WSL section)." >&2
    exit 1
fi

PUBLISH_DIR="$(mktemp -d)"
trap 'rm -rf "$PUBLISH_DIR"' EXIT

echo "== Publishing $RID binaries =="
dotnet publish src/Healer.Host -c Release -r "$RID" --self-contained true -p:PublishAot=true -o "$PUBLISH_DIR/host"
# Healer.Setup/Status are self-contained but NOT AOT (Terminal.Gui's reflection use is fine for a
# one-shot tool). Without -p:PublishSingleFile, "--self-contained true" alone still produces ~220
# loose files (the apphost plus every runtime/dependency DLL and native library) — shipping only the
# named executable, as a naive script would, silently ships a binary missing everything it needs to
# actually run. PublishSingleFile + IncludeNativeLibrariesForSelfExtract bundles it all into one
# real executable.
dotnet publish src/Healer.Setup -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PUBLISH_DIR/setup"
dotnet publish src/Healer.Status -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$PUBLISH_DIR/status"

echo "== Assembling payload at $OUTPUT_DIR =="
mkdir -p "$OUTPUT_DIR/.extract"
# 1777 (world-writable + sticky bit, same as /tmp) because healer-setup/healer-status run as many
# different users across their various call sites (root during postinst, root or a human via
# `sudo`, whatever `healer-first-run.sh` runs as) — this directory must be writable by all of them.
chmod 1777 "$OUTPUT_DIR/.extract"

cp "$PUBLISH_DIR/host/healer" "$OUTPUT_DIR/healer"
# Native AOT only compiles MANAGED code to native — a genuinely native dependency like SQLite's C
# library does NOT get folded into the single `healer` executable, and still ships as its own .so
# file next to it. Missing this file is exactly what caused "sqlite error 14: unable to open
# database file" at runtime the first time this was packaged — the daemon started fine (the AOT
# executable itself is complete), but SQLite's native provider had nothing to dlopen the moment it
# was actually used.
cp "$PUBLISH_DIR/host/libe_sqlite3.so" "$OUTPUT_DIR/libe_sqlite3.so"
chmod 755 "$OUTPUT_DIR/libe_sqlite3.so"

# healer-setup/healer-status are PublishSingleFile + IncludeNativeLibrariesForSelfExtract bundles
# (see above) — at every run, they need to extract their native libraries to a writable directory,
# and confirmed BY ACTUALLY RUNNING A BUILT PACKAGE: .NET's automatic fallback search for that
# directory (HOME, TMPDIR, etc.) is not reliable across the different contexts these tools run in —
# it failed with "a read-write cache directory couldn't be created" in this project's real
# deployment paths. The fix is to pin an explicit, always-writable extraction directory rather than
# depend on that guesswork: rename the real executables to `.bin`, and ship a thin wrapper script
# under the original name that sets DOTNET_BUNDLE_EXTRACT_BASE_DIR before exec-ing it.
cp "$PUBLISH_DIR/setup/healer-setup" "$OUTPUT_DIR/healer-setup.bin"
cp "$PUBLISH_DIR/status/healer-status" "$OUTPUT_DIR/healer-status.bin"
chmod 755 "$OUTPUT_DIR/healer-setup.bin" "$OUTPUT_DIR/healer-status.bin"

for name in healer-setup healer-status; do
    printf '#!/bin/sh\nexport DOTNET_BUNDLE_EXTRACT_BASE_DIR="/opt/healer/.extract"\nexec "/opt/healer/%s.bin" "$@"\n' "$name" >"$OUTPUT_DIR/$name"
    chmod 755 "$OUTPUT_DIR/$name"
done

# healer.service and healer-first-run.sh ship in this SAME flat directory rather than at their
# eventual system locations (/etc/systemd/system, /etc/profile.d) — callers relocate/copy them from
# here as their own packaging format requires (e.g. build-deb.sh moves healer-first-run.sh out to
# /etc/profile.d since dpkg can track arbitrary install paths; the tarball path leaves both in place
# here and lets bootstrap.sh do that copy at install time instead, since `tar -xzf` only ever
# extracts to one root). healer.service specifically lives next to healer-setup rather than at the
# Debian-canonical /lib/systemd/system path in EITHER case — see docs/ARCHITECTURE.md for why
# (Healer.Setup.Logic.HealerInstaller already expects it there and owns the whole systemd lifecycle
# itself, so shipping it here needed zero application-code changes).
cp deploy/healer.service "$OUTPUT_DIR/healer.service"
cp deploy/healer-first-run.sh "$OUTPUT_DIR/healer-first-run.sh"
chmod 644 "$OUTPUT_DIR/healer.service"
chmod 755 "$OUTPUT_DIR/healer" "$OUTPUT_DIR/healer-first-run.sh"

echo "== Payload ready at $OUTPUT_DIR =="
