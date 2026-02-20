param(
  [string]$BaseUrl = "http://localhost"
)

$ErrorActionPreference = "Stop"

function Invoke-Api {
  param(
    [string]$Method,
    [string]$Url,
    [hashtable]$Headers = @{},
    [string]$Body = $null,
    [int]$ExpectedStatus
  )

  try {
    $params = @{ Method = $Method; Uri = $Url; Headers = $Headers }
    if ($null -ne $Body) {
      $params.ContentType = "application/json"
      $params.Body = $Body
    }

    $response = Invoke-WebRequest @params
    $statusCode = [int]$response.StatusCode
    $content = $response.Content
  }
  catch {
    if ($_.Exception.Response) {
      $statusCode = [int]$_.Exception.Response.StatusCode
      $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
      $content = $reader.ReadToEnd()
      $reader.Close()
    }
    else {
      throw
    }
  }

  Write-Host "$Method $Url -> $statusCode"
  if ($content) { Write-Host $content }

  if ($statusCode -ne $ExpectedStatus) {
    throw "Ожидался HTTP $ExpectedStatus, получен $statusCode для $Url"
  }

  return $content
}

$registerEmail = "smoke_$(Get-Random)@example.com"
$registerPassword = "SmokePass123"

Write-Host "`n== health (200) =="
Invoke-Api -Method "GET" -Url "$BaseUrl/api/health" -ExpectedStatus 200 | Out-Null

Write-Host "`n== register (201) =="
$registerBody = @{ email = $registerEmail; password = $registerPassword; role = "captain" } | ConvertTo-Json -Compress
Invoke-Api -Method "POST" -Url "$BaseUrl/api/auth/register" -Body $registerBody -ExpectedStatus 201 | Out-Null

Write-Host "`n== login (200) =="
$loginBody = @{ email = $registerEmail; password = $registerPassword } | ConvertTo-Json -Compress
$loginContent = Invoke-Api -Method "POST" -Url "$BaseUrl/api/auth/login" -Body $loginBody -ExpectedStatus 200
$loginJson = $loginContent | ConvertFrom-Json
if (-not $loginJson.token) {
  throw "В ответе login отсутствует token"
}

$headers = @{ Authorization = "Bearer $($loginJson.token)" }

Write-Host "`n== auth/me (200) =="
Invoke-Api -Method "GET" -Url "$BaseUrl/api/auth/me" -Headers $headers -ExpectedStatus 200 | Out-Null

Write-Host "`n== tournaments (200) =="
Invoke-Api -Method "GET" -Url "$BaseUrl/api/tournament" -ExpectedStatus 200 | Out-Null

Write-Host "`n== teams (200) =="
Invoke-Api -Method "GET" -Url "$BaseUrl/api/teams" -ExpectedStatus 200 | Out-Null

Write-Host "`nSmoke test passed ✅"
