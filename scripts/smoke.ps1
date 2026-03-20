$ErrorActionPreference = 'Stop'
Write-Host 'GET /api/health'
Invoke-WebRequest -Uri 'http://localhost/api/health' | Out-Null
Write-Host 'GET /'
Invoke-WebRequest -Uri 'http://localhost/' | Out-Null
Write-Host 'Smoke OK'
