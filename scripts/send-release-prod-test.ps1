param(
    [string]$ApiBase = "https://api-mambasplit.mambatech.io",
    [string]$Email = "jmendoza89@gmail.com",
    [string]$DisplayName = "Julio",
    [string]$AppLink = "https://ms.mambatech.io",
    [string]$AssetBaseUrl = "https://api-mambasplit.mambatech.io"
)

# Single-recipient release email test - PRODUCTION
# Usage: .\scripts\send-release-prod-test.ps1
# Sends the release-v1.2.0 email to one inbox so images and links can be verified before the full blast.

$ErrorActionPreference = "Stop"

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

Write-Host "Login OK. Sending single-recipient release email test..."
$sendBody = @{
    templateKey = "release-v1.2.0"
    to = @($Email)
    model = @{
        firstName = $DisplayName
        appLink = $AppLink
        screenshotMain = "$($AssetBaseUrl.TrimEnd('/'))/internal/email-preview-assets/screenshotMain.png"
        screenshotGroup = "$($AssetBaseUrl.TrimEnd('/'))/internal/email-preview-assets/screenshotGroup.png"
    }
} | ConvertTo-Json -Depth 4

$sendResp = Invoke-RestMethod -Method Post `
    -Uri "$ApiBase/api/v1/internal/email/send" `
    -ContentType "application/json" `
    -Headers @{ Authorization = "Bearer $token" } `
    -Body $sendBody

Write-Host "Done:" ($sendResp | ConvertTo-Json)
