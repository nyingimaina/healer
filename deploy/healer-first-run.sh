#!/bin/sh
# Installed to /etc/profile.d/healer-first-run.sh by bootstrap.sh, or shipped directly at that path
# by the .deb package built via deploy/build-deb.sh — either way, the logic below is identical.
#
# Runs on every interactive login until Healer has been configured, so a non-technical user never
# needs to remember a command: they SSH in, and the setup wizard is just... there. This is what
# decouples "staged onto the box" (which can happen non-interactively, e.g. EC2 user-data at boot,
# with no terminal attached to run a TUI on) from "configured" (which genuinely needs a human, for
# the Telegram token if nothing else) — and it doubles as a safety net if someone's SSH connection
# drops mid-wizard before the Apply step: nothing was written yet, so their next login just offers
# the wizard again.
#
# `[ -t 0 ]` is the guard that keeps this out of the way of non-interactive sessions (scp, ansible,
# `ssh host command`, cron) — it only fires when stdin is an actual terminal.

if [ -t 0 ] && [ ! -f /etc/healer/healer.json ] && [ -x /opt/healer/healer-setup ]; then
    if [ "$(id -u)" -eq 0 ]; then
        /opt/healer/healer-setup
    else
        echo "Healer isn't set up yet on this box. Run: sudo healer-setup"
    fi
fi
