#!/bin/sh
# Run as whoever owns config/authelia on the host.
#
# auth-init writes users.yml with mode 600, owned by that directory's owner:
# the deploy user, whatever uid that is. A fixed image user (uid 1000) can
# read it only when the deploy user happens to be uid 1000 too. On a host
# where it is not, every page that touches accounts returned HTTP 500
# (PermissionError on /authelia/users.yml).
set -e
uid=$(stat -c %u /authelia)
gid=$(stat -c %g /authelia)
if [ "$uid" = 0 ]; then
  # Never run the web app as root. A root-owned config/authelia cannot be
  # written by the panel; say so instead of failing on the first request.
  echo "admin-panel: config/authelia is owned by root; account changes will fail. chown it to the deploy user." >&2
  uid=$(id -u panel); gid=$(id -g panel)
fi
exec setpriv --reuid "$uid" --regid "$gid" --clear-groups -- "$@"
