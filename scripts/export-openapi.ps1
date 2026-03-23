param(
    [string]$ApiBaseUrl = "http://localhost:8080",
    [string]$DocumentName = "v1",
    [string]$OutputPath = "docs/openapi/openapi-v1.json",
    [switch]$Timestamped
)

$ErrorActionPreference = "Stop"

function Normalize-Url {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value.TrimEnd('/')
}

$baseUrl = Normalize-Url -Value $ApiBaseUrl
$swaggerUrl = "$baseUrl/swagger/$DocumentName/swagger.json"

try {
    $null = [Uri]$swaggerUrl
} catch {
    Write-Error "Invalid URL: $swaggerUrl"
}

$finalOutputPath = $OutputPath
if ($Timestamped) {
    $directory = Split-Path -Parent $OutputPath
    $fileName = Split-Path -Leaf $OutputPath
    $nameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($fileName)
    $extension = [System.IO.Path]::GetExtension($fileName)
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $finalOutputPath = Join-Path $directory "$nameWithoutExtension-$stamp$extension"
}

$absoluteOutputPath = if ([System.IO.Path]::IsPathRooted($finalOutputPath)) {
    $finalOutputPath
} else {
    Join-Path (Get-Location) $finalOutputPath
}

$outputDirectory = Split-Path -Parent $absoluteOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

Write-Host "Fetching OpenAPI document from $swaggerUrl ..."

try {
    $document = Invoke-RestMethod -Uri $swaggerUrl -Method Get -TimeoutSec 30
} catch {
    Write-Error "Failed to fetch OpenAPI document. Ensure the API is running and Swagger is enabled. URL: $swaggerUrl"
}

$json = $document | ConvertTo-Json -Depth 100
Set-Content -Path $absoluteOutputPath -Value $json -Encoding utf8

Write-Host "OpenAPI snapshot written to: $absoluteOutputPath"