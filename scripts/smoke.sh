#!/usr/bin/env bash
set -e
curl -fsS http://localhost/api/health >/dev/null
curl -fsS http://localhost/ >/dev/null
echo 'Smoke OK'
