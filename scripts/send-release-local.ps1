param(
    [string]$ApiBase = "http://localhost:8080",
    [string]$Email,
    [string]$DisplayName = "Julio",
    [string]$AppLink = "http://localhost:5173",
    [string]$AssetBaseUrl
)

# One-time release email test - LOCAL
# Usage: .\scripts\send-release-local.ps1
# Reads the default authorized email from local appsettings unless -Email is provided.

$ErrorActionPreference = "Stop"

function Get-DefaultInternalEmail {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot = Resolve-Path (Join-Path $scriptDir "..")
    $configPaths = @(
        (Join-Path $repoRoot "src\MambaSplit.Api\appsettings.local.json"),
        (Join-Path $repoRoot "src\MambaSplit.Api\appsettings.Development.json")
    )

    foreach ($configPath in $configPaths) {
        if (-not (Test-Path $configPath)) {
            continue
        }

        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        $allowedEmails = @($config.Email.InternalAllowedEmails)
        if ($allowedEmails.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($allowedEmails[0])) {
            return $allowedEmails[0]
        }
    }

    throw "Could not find Email:InternalAllowedEmails in appsettings.local.json or appsettings.Development.json."
}

if ([string]::IsNullOrWhiteSpace($Email)) {
    $Email = Get-DefaultInternalEmail
}

if ([string]::IsNullOrWhiteSpace($AssetBaseUrl)) {
    $AssetBaseUrl = $ApiBase.TrimEnd('/')
}

$Password = Read-Host -Prompt "Password for $Email" -AsSecureString
$PlainPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))

Write-Host "`nLogging in..."
$loginBody = @{ email = $Email; password = $PlainPassword } | ConvertTo-Json
$token = $null

try {
    $loginResp = Invoke-RestMethod -Method Post `
        -Uri "$ApiBase/api/v1/auth/login" `
        -ContentType "application/json" `
        -Body $loginBody
    $token = $loginResp.accessToken
}
catch {
    $statusCode = if ($_.Exception.Response) { $_.Exception.Response.StatusCode.value__ } else { $null }
    if ($statusCode -ne 401) {
        throw
    }

    Write-Host "Login returned 401. Trying signup for a fresh local database..."
    $signupBody = @{
        email = $Email
        password = $PlainPassword
        displayName = $DisplayName
    } | ConvertTo-Json

    try {
        $signupResp = Invoke-RestMethod -Method Post `
            -Uri "$ApiBase/api/v1/auth/signup" `
            -ContentType "application/json" `
            -Body $signupBody
        $token = $signupResp.accessToken
    }
    catch {
        throw "Login failed, and signup also failed. Check whether the local user already exists with a different password."
    }
}

if (-not $token) {
    Write-Error "Login/signup failed."
    exit 1
}

Write-Host "Login OK. Sending release email..."

if ($AssetBaseUrl -match '^(https?://)?(localhost|127\.0\.0\.1)(:\d+)?(/|$)') {
    Write-Warning "Screenshot URLs point to localhost. External email clients like Gmail will not be able to load those images."
}

if ($AppLink -match '^(https?://)?(localhost|127\.0\.0\.1)(:\d+)?(/|$)') {
    Write-Warning "App link points to localhost. Opening the CTA from an external inbox will only work on this machine."
}

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

try {
    $sendResp = Invoke-RestMethod -Method Post `
        -Uri "$ApiBase/api/v1/internal/email/send" `
        -ContentType "application/json" `
        -Headers @{ Authorization = "Bearer $token" } `
        -Body $sendBody
}
catch {
    $statusCode = if ($_.Exception.Response) { $_.Exception.Response.StatusCode.value__ } else { $null }
    if ($statusCode -eq 403) {
        throw "Email '$Email' is authenticated but not authorized for /api/v1/internal/email/send. Add it to Email:InternalAllowedEmails or run the script with an allowed email."
    }

    throw
}

Write-Host "Done:" ($sendResp | ConvertTo-Json)
