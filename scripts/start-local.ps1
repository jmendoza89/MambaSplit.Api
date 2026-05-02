param(
    [switch]$NoRestore,
    [switch]$SkipDocker,
    [switch]$Background,
    [switch]$NoLaunchBrowser,
    [string]$ApiUrl = "http://localhost:8080",
    [string]$LaunchPath = "swagger",
    [string]$LogPrefix = "api-local"
)

$ErrorActionPreference = "Stop"

function Require-Command {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Write-Error "$Name is not installed or not on PATH."
    }
}

function Get-LaunchUrl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $BaseUrl
    }

    if ($Path -match "^https?://") {
        return $Path
    }

    return "$($BaseUrl.TrimEnd('/'))/$($Path.TrimStart('/'))"
}

function Start-BrowserWhenReady {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url
    )

    if ($NoLaunchBrowser) {
        return
    }

    Write-Host "Swagger will open at $Url when the API is ready..."
    Start-Job -Name "MambaSplitApiSwaggerLauncher" -ScriptBlock {
        param([string]$ReadyUrl)

        for ($attempt = 1; $attempt -le 60; $attempt++) {
            try {
                Invoke-WebRequest -Uri $ReadyUrl -UseBasicParsing -TimeoutSec 2 | Out-Null
                Start-Process $ReadyUrl
                return
            }
            catch {
                Start-Sleep -Seconds 1
            }
        }
    } -ArgumentList $Url | Out-Null
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..")
$apiProjectPath = Join-Path $repoRoot "src\\MambaSplit.Api\\MambaSplit.Api.csproj"
Set-Location $repoRoot

Write-Host "Checking .NET SDK..."
Require-Command -Name "dotnet"

if (-not $SkipDocker) {
    Write-Host "Checking Docker CLI..."
    Require-Command -Name "docker"

    Write-Host "Checking Docker daemon..."
    cmd /c "docker info >NUL 2>&1"
    if ($LASTEXITCODE -ne 0) {
        $dockerInfoOutput = cmd /c "docker info 2>&1"
        if ($dockerInfoOutput -match "permission denied.*docker_engine") {
            Write-Error "Docker is installed but this shell cannot access the Docker engine (npipe/docker_engine permission denied). Run from a terminal with Docker access or use -SkipDocker if DB is already running."
        }

        Write-Error "Docker daemon is not running or not reachable. Start Docker Desktop, then run this script again."
    }

    Write-Host "Checking Docker Compose..."
    cmd /c "docker compose version >NUL 2>&1"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker Compose is not available. Install or enable Docker Compose, then run this script again."
    }

    $existingDbContainer = docker ps -a --filter "name=^/mambasplit_db$" --format "{{.ID}}|{{.Status}}"
    if ($existingDbContainer) {
        $containerParts = $existingDbContainer.Split('|', 2)
        $containerId = $containerParts[0]
        $containerStatus = if ($containerParts.Length -gt 1) { $containerParts[1] } else { "unknown" }

        if ($containerStatus -like "Up*") {
            Write-Host "Reusing existing Postgres container mambasplit_db ($containerId)."
        }
        else {
            Write-Host "Starting existing Postgres container mambasplit_db ($containerId)..."
            docker start $containerId | Out-Null
        }
    }
    else {
        Write-Host "Starting Postgres container (docker compose up -d db)..."
        docker compose up -d db
    }
}

try {
    $targetUri = [Uri]$ApiUrl
} catch {
    Write-Error "Invalid -ApiUrl value '$ApiUrl'. Example: http://localhost:8080"
}

if ($targetUri.Port -le 0) {
    Write-Error "Unable to determine port from -ApiUrl '$ApiUrl'."
}

$portInUse = Get-NetTCPConnection -State Listen -LocalPort $targetUri.Port -ErrorAction SilentlyContinue
if ($portInUse) {
    $pids = ($portInUse | Select-Object -ExpandProperty OwningProcess -Unique) -join ", "
    Write-Error "Port $($targetUri.Port) is already in use (PID(s): $pids). Stop that process or run with a different -ApiUrl."
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:ASPNETCORE_URLS = $ApiUrl

# Avoid Event Log permission issues in restricted shells.
$env:Logging__EventLog__LogLevel__Default = "None"

$dotnetArgs = @("run", "--project", $apiProjectPath)
if ($NoRestore) {
    $dotnetArgs += "--no-restore"
}

$launchUrl = Get-LaunchUrl -BaseUrl $ApiUrl -Path $LaunchPath

Write-Host "Starting API with local profile on $ApiUrl..."
Start-BrowserWhenReady -Url $launchUrl

if ($Background) {
    $logsDir = Join-Path $repoRoot "logs"
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $outLog = Join-Path $logsDir "$LogPrefix-$stamp.out.log"
    $errLog = Join-Path $logsDir "$LogPrefix-$stamp.err.log"

    $proc = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList $dotnetArgs `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $outLog `
        -RedirectStandardError $errLog `
        -PassThru

    Write-Host "API started in background."
    Write-Host "PID: $($proc.Id)"
    Write-Host "stdout: $outLog"
    Write-Host "stderr: $errLog"
    Write-Host "Swagger: $launchUrl"
    exit 0
}

& "dotnet" @dotnetArgs
