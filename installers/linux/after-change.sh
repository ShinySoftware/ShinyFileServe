#!/bin/sh
# Menus and icon themes cache what is installed; refresh them so FileServe appears (or disappears)
# without logging out. Each tool is optional - a minimal system without them is not an error.
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
exit 0
