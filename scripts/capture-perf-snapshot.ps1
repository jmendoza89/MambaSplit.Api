param(
    [string]$Checkpoint = "",
    [string]$ApiProcessName = "MambaSplit.Api",
    [string]$DbContainerName = "mambasplit_db"
)

$timestamp = Get-Date -Format "yyyy-MM-ddTHH:mm:sszzz"
$label = if ([string]::IsNullOrWhiteSpace($Checkpoint)) { "Perf snapshot" } else { $Checkpoint }

Write-Host "== $label =="
Write-Host "Timestamp: $timestamp"
Write-Host ""

$apiProcesses = Get-Process -Name $ApiProcessName -ErrorAction SilentlyContinue

if ($apiProcesses) {
    Write-Host "API process"
    $apiProcesses |
        Select-Object Id,
            ProcessName,
            @{Name = "WorkingSetMB"; Expression = { [math]::Round($_.WorkingSet64 / 1MB, 1) } },
            @{Name = "PrivateMB"; Expression = { [math]::Round($_.PrivateMemorySize64 / 1MB, 1) } },
            CPU |
        Format-Table -AutoSize
} else {
    Write-Host "API process"
    Write-Host "No process found named '$ApiProcessName'."
    Write-Host ""
}

Write-Host "DB container"
$dockerStats = docker stats $DbContainerName --no-stream --format "{{.Name}}|{{.MemUsage}}|{{.CPUPerc}}" 2>&1

if ($LASTEXITCODE -eq 0 -and $dockerStats) {
    $parts = $dockerStats -split "\|"
    [pscustomobject]@{
        Name = $parts[0]
        MemUsage = $parts[1]
        CPU = $parts[2]
    } | Format-Table -AutoSize
} else {
    Write-Host $dockerStats
}
