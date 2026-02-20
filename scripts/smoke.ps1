param([string]$BaseUrl = "http://localhost")

$registerEmail = "smoke_$(Get-Random)@example.com"
$registerPassword = "SmokePass123"

Write-Host "`n== health =="
curl -i "$BaseUrl/api/health"

Write-Host "`n== register (expected 201) =="
curl -i -Method Post "$BaseUrl/api/auth/register" -ContentType "application/json" -Body "{\"email\":\"$registerEmail\",\"password\":\"$registerPassword\",\"role\":\"captain\"}"

Write-Host "`n== login (expected 200) =="
curl -i -Method Post "$BaseUrl/api/auth/login" -ContentType "application/json" -Body "{\"email\":\"$registerEmail\",\"password\":\"$registerPassword\"}"

Write-Host "`n== tournaments =="
curl -i "$BaseUrl/api/tournament"

Write-Host "`n== teams =="
curl -i "$BaseUrl/api/teams"
