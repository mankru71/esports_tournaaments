param(
  [string]$BaseUrl = "http://localhost"
)

Write-Host "`n== health =="
curl "$BaseUrl/api/health"

Write-Host "`n== login (expected 401/200 depending on user existence) =="
curl -Method Post "$BaseUrl/api/auth/login" -ContentType "application/json" -Body '{"email":"demo@example.com","password":"demo12345"}'

Write-Host "`n== tournaments =="
curl "$BaseUrl/api/tournament"
