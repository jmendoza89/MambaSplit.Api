param(
    [string]$TemplateAgentsPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $TemplateAgentsPath) {
    $TemplateAgentsPath = Join-Path $repoRoot '..\agent-templates\agents'
}

$templatePath = Resolve-Path $TemplateAgentsPath
$targetPath = Join-Path $repoRoot '.github\agents'

New-Item -ItemType Directory -Path $targetPath -Force | Out-Null

Get-ChildItem -Path $targetPath -Filter '*.agent.md' -File | Remove-Item -Force
Copy-Item -Path (Join-Path $templatePath '*.agent.md') -Destination $targetPath -Force

Write-Host "Synced agent files from '$templatePath' to '$targetPath'."
