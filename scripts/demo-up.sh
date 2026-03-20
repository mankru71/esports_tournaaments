#!/usr/bin/env bash
set -e
docker compose up -d --build
sleep 8
curl -fsS -X POST http://localhost/api/demo/seed -H 'Authorization: Bearer demo' >/dev/null || true
echo 'Open http://localhost/'
