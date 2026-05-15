#!/usr/bin/env bash
set -euo pipefail
curl -fsS http://localhost/api/health >/dev/null
curl -fsS http://localhost/api/discord/status >/dev/null
curl -fsS http://localhost/ >/dev/null
echo 'Smoke OK'
