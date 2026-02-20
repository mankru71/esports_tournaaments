#!/usr/bin/env bash
set -euo pipefail

BASE_URL=${1:-http://localhost}

printf '\n== health ==\n'
curl -fsS "$BASE_URL/api/health" && echo

printf '\n== login (expected 401/200 depending on user existence) ==\n'
curl -sS -o /tmp/login.out -w "HTTP %{http_code}\n" -X POST "$BASE_URL/api/auth/login" \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@example.com","password":"demo12345"}'
cat /tmp/login.out && echo

printf '\n== tournaments ==\n'
curl -fsS "$BASE_URL/api/tournament" && echo
