#!/bin/sh
# Starts as root only long enough to hand /data to PUID:PGID, then drops to that user for good. The
# files people share are never touched here - their ownership is the host's business.
set -e

umask "${UMASK:-002}"

if [ "$(id -u)" = "0" ]; then
    PUID="${PUID:-99}"
    PGID="${PGID:-100}"

    mkdir -p "$FILESERVE_DATA"
    # Only what is not already theirs, so a large packages folder is not walked on every start.
    find "$FILESERVE_DATA" \( \! -user "$PUID" -o \! -group "$PGID" \) -exec chown "$PUID:$PGID" {} +

    exec setpriv --reuid="$PUID" --regid="$PGID" --clear-groups -- dotnet /app/fileserve.dll "$@"
fi

# Already started as someone else (docker run --user): run as that user and let them own the result.
exec dotnet /app/fileserve.dll "$@"
