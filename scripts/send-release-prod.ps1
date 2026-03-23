# One-time release email blast - PRODUCTION
# Usage: .\scripts\send-release-prod.ps1
# Logs in as your account, then triggers the blast to all users.
# Run this only after the single-recipient prod test looks correct.

$ErrorActionPreference = "Stop"

$ApiBase = "https://api-mambasplit.mambatech.io"
$Email = "jmendoza89@gmail.com"

$Password = Read-Host -Prompt "Password for $Email" -AsSecureString
$PlainPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))

Write-Host "`nLogging in to production..."
$loginBody = @{ email = $Email; password = $PlainPassword } | ConvertTo-Json
$loginResp = Invoke-RestMethod -Method Post `
    -Uri "$ApiBase/api/v1/auth/login" `
    -ContentType "application/json" `
    -Body $loginBody

$token = $loginResp.accessToken
if (-not $token) {
    Write-Error "Login failed. Check your password."
    exit 1
}

Write-Host "Login OK. Sending release email to ALL users..."
$sendResp = Invoke-RestMethod -Method Post `
    -Uri "$ApiBase/api/v1/internal/email/send-release-v1.2.0" `
    -ContentType "application/json" `
    -Headers @{ Authorization = "Bearer $token" }

Write-Host "Done:" ($sendResp | ConvertTo-Json)
