#!/bin/sh
# Clears the sentinel file written by healer-disable (or by Healer itself, via
# Healer.Core.Decision.EmergencyActionRateBreaker tripping) so Healer resumes taking automatic
# action on its next tick. Deliberately no auto-resume anywhere in this system -- this is the one
# way back, and it always prints WHY Healer was disabled first, so nobody clears an emergency stop
# blind.
set -e

if [ "$(id -u)" -ne 0 ]; then
    echo "healer-enable must run as root (it removes a file from Healer's config directory)." >&2
    exit 1
fi

CONFIG_PATH="${HEALER_CONFIG_PATH:-/etc/healer/healer.json}"
CONFIG_DIR="$(dirname "$CONFIG_PATH")"
SENTINEL="$CONFIG_DIR/DISABLED"

if [ ! -f "$SENTINEL" ]; then
    echo "Healer is already enabled -- no disable sentinel found at $SENTINEL."
    exit 0
fi

echo "Healer was disabled for this reason:"
echo "---"
cat "$SENTINEL"
echo "---"

rm -f "$SENTINEL"
echo "Healer is now ENABLED again and will resume automatic action on its next tick."
