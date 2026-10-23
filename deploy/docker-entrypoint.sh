#!/bin/sh
# Container start (see Dockerfile). A freshly mounted volume belongs to root, so the data folder is
# handed to the unprivileged "app" user first; the application itself never runs as root.
set -eu

mkdir -p /data
chown -R app:app /data
chmod 700 /data   # database, demo PDFs and the login keys: readable only by the app user

exec setpriv --reuid=app --regid=app --init-groups dotnet /app/BewerbungsPilot.dll "$@"
