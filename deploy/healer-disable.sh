#!/bin/sh
# Immediately stops Healer from taking any further automatic action -- without touching config or
# systemd, and without restarting the daemon. Detection and Telegram alerts keep happening; every
# mutating action is skipped and recorded to history as "Disabled" instead. Reverse with
# `sudo healer-enable`.
#
# Shares one sentinel file (<config-directory>/DISABLED) with the self-tripping emergency breaker
# (see Healer.Core.Decision.EmergencyActionRateBreaker) and with the Ctrl+D handler in healer-status
# -- whichever origin tripped it, this is the same file, so there's one mental model and one command
# to clear it regardless of why Healer stopped.
set -e

if [ "$(id -u)" -ne 0 ]; then
    echo "healer-disable must run as root (it writes into Healer's config directory)." >&2
    exit 1
fi

CONFIG_PATH="${HEALER_CONFIG_PATH:-/etc/healer/healer.json}"
CONFIG_DIR="$(dirname "$CONFIG_PATH")"
SENTINEL="$CONFIG_DIR/DISABLED"

REASON="$*"
if [ -z "$REASON" ]; then
    REASON="Manually disabled via healer-disable (no reason given)."
fi

mkdir -p "$CONFIG_DIR"
printf '%s\n(disabled at %s)\n' "$REASON" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" >"$SENTINEL"

echo "Healer is now DISABLED."
echo "It will keep detecting and alerting as usual, but will take NO automatic action."
echo "Reason recorded: $REASON"
echo "Run 'sudo healer-enable' when you're ready to resume."
