$ErrorActionPreference = 'Stop'
docker compose up -d --build
Start-Sleep -Seconds 8
try {
  Invoke-RestMethod -Method Post -Uri 'http://localhost/api/demo/seed' -Headers @{ Authorization = 'Bearer demo' } | Out-Null
} catch {
  Write-Host 'demo seed endpoint недоступен или требует авторизацию. Приложение всё равно поднято.'
}
Write-Host 'Open http://localhost/'
