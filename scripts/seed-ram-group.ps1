#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Seeds the local database with the "RAM Group" test group containing ~600 expenses
    spread organically across two users over ~18 months.

    Users:
      jmendoza (Julio C. Mendoza) : c8aa8a33-b533-4def-8985-6860915071f1
      julio    (Julio)            : dc1c5a11-fc7b-4a79-a970-82dabf159581

    Run from repo root:
        .\scripts\seed-ram-group.ps1
#>

$ErrorActionPreference = 'Stop'

$JMENDOZA = 'c8aa8a33-b533-4def-8985-6860915071f1'
$JULIO    = 'dc1c5a11-fc7b-4a79-a970-82dabf159581'
$GROUP_ID = [guid]::NewGuid().ToString()

# ── Expense catalogue ──────────────────────────────────────────────────────────
# Each entry: description, min_cents, max_cents, payer_weights (jmendoza, julio)
$catalogue = @(
    # Groceries — both pay roughly equally
    @{ desc='Groceries - Publix';          min=3000;  max=12000; w=@(0.5, 0.5) }
    @{ desc='Groceries - Whole Foods';     min=4000;  max=15000; w=@(0.4, 0.6) }
    @{ desc='Groceries - Trader Joe''s';   min=2500;  max=9000;  w=@(0.55,0.45) }
    @{ desc='Costco run';                  min=8000;  max=25000; w=@(0.6, 0.4) }
    @{ desc='Target - household items';    min=2000;  max=8000;  w=@(0.5, 0.5) }
    # Utilities — jmendoza pays more often
    @{ desc='Electric bill';               min=9000;  max=18000; w=@(0.7, 0.3) }
    @{ desc='Internet bill';               min=5000;  max=8000;  w=@(0.65,0.35) }
    @{ desc='Water bill';                  min=4000;  max=7000;  w=@(0.6, 0.4) }
    @{ desc='Phone plan';                  min=6000;  max=12000; w=@(0.5, 0.5) }
    # Dining out — julio pays more often
    @{ desc='Dinner - Chili''s';           min=2500;  max=7000;  w=@(0.35,0.65) }
    @{ desc='Lunch - Chipotle';            min=1200;  max=3500;  w=@(0.4, 0.6) }
    @{ desc='Breakfast - IHOP';            min=1500;  max=4000;  w=@(0.45,0.55) }
    @{ desc='Coffee run';                  min=500;   max=2000;  w=@(0.5, 0.5) }
    @{ desc='Sushi night';                 min=4000;  max=10000; w=@(0.3, 0.7) }
    @{ desc='Pizza delivery';              min=2000;  max=5000;  w=@(0.5, 0.5) }
    @{ desc='Food delivery - DoorDash';    min=1800;  max=5500;  w=@(0.45,0.55) }
    # Home
    @{ desc='Rent';                        min=80000; max=130000;w=@(0.5, 0.5) }
    @{ desc='Household supplies';          min=1500;  max=6000;  w=@(0.55,0.45) }
    @{ desc='Cleaning supplies';           min=1000;  max=3500;  w=@(0.5, 0.5) }
    @{ desc='Home repair';                 min=5000;  max=30000; w=@(0.6, 0.4) }
    @{ desc='Amazon order';               min=1500;  max=12000; w=@(0.5, 0.5) }
    # Entertainment
    @{ desc='Movie tickets';               min=2000;  max=5000;  w=@(0.5, 0.5) }
    @{ desc='Netflix subscription';        min=1500;  max=2500;  w=@(0.5, 0.5) }
    @{ desc='Spotify Family';              min=1600;  max=1800;  w=@(0.5, 0.5) }
    @{ desc='Concert tickets';             min=5000;  max=20000; w=@(0.4, 0.6) }
    # Transport
    @{ desc='Gas station';                 min=4000;  max=8000;  w=@(0.55,0.45) }
    @{ desc='Uber ride';                   min=800;   max=3500;  w=@(0.5, 0.5) }
    @{ desc='Car insurance';               min=12000; max=20000; w=@(0.5, 0.5) }
    @{ desc='Parking';                     min=500;   max=3000;  w=@(0.5, 0.5) }
    # Health / personal
    @{ desc='Pharmacy';                    min=1000;  max=6000;  w=@(0.5, 0.5) }
    @{ desc='Doctor co-pay';               min=3000;  max=8000;  w=@(0.5, 0.5) }
    @{ desc='Gym membership';              min=4000;  max=7000;  w=@(0.5, 0.5) }
    @{ desc='Haircut';                     min=1500;  max=4000;  w=@(0.5, 0.5) }
    # Travel / misc
    @{ desc='Hotel - trip';                min=10000; max=35000; w=@(0.5, 0.5) }
    @{ desc='Airfare';                     min=15000; max=50000; w=@(0.5, 0.5) }
    @{ desc='Miscellaneous';               min=500;   max=5000;  w=@(0.5, 0.5) }
)

$rng = [System.Random]::new(42)  # fixed seed for reproducibility

function Get-RandomPayer {
    param($weights)
    if ($rng.NextDouble() -lt $weights[0]) { return $JMENDOZA } else { return $JULIO }
}

function Get-RandomAmount {
    param($min, $max)
    return $rng.Next($min, $max)
}

# Generate 600 expenses spread over 18 months ending today (2026-05-02)
$endDate   = [datetime]::new(2026, 5, 2)
$startDate = $endDate.AddMonths(-18)
$totalSpan = ($endDate - $startDate).TotalSeconds

$expenses = [System.Collections.Generic.List[hashtable]]::new()
for ($i = 0; $i -lt 600; $i++) {
    $cat    = $catalogue[$rng.Next(0, $catalogue.Count)]
    $payer  = Get-RandomPayer $cat.w
    $other  = if ($payer -eq $JMENDOZA) { $JULIO } else { $JMENDOZA }
    $amount = Get-RandomAmount $cat.min $cat.max
    $half   = [math]::Floor($amount / 2)
    $otherHalf = $amount - $half

    # Organic timestamp: slightly more expenses on weekends / evenings
    $offsetSec = [long]($rng.NextDouble() * $totalSpan)
    $dt        = $startDate.AddSeconds($offsetSec)
    # add some evening skew for meals
    $ts = $dt.ToString('yyyy-MM-dd HH:mm:ss') + '+00'

    $expId     = [guid]::NewGuid().ToString()
    $iKey      = "seed-$i-" + $expId.Substring(0,8)

    $expenses.Add(@{
        id          = $expId
        group_id    = $GROUP_ID
        payer       = $payer
        created_by  = $payer
        desc        = $cat.desc
        amount      = $amount
        half_payer  = $half
        half_other  = $otherHalf
        other       = $other
        ts          = $ts
        ikey        = $iKey
    })
}

# Sort by timestamp for natural order
$expenses = $expenses | Sort-Object { $_['ts'] }

# ── Build SQL ──────────────────────────────────────────────────────────────────
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("-- RAM Group seed: 600 organic expenses")
[void]$sb.AppendLine("-- Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("-- Group ID: $GROUP_ID")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("BEGIN;")
[void]$sb.AppendLine("")

# Group
[void]$sb.AppendLine("INSERT INTO groups (id, name, created_by, created_at) VALUES (")
[void]$sb.AppendLine("    '$GROUP_ID',")
[void]$sb.AppendLine("    'RAM Group',")
[void]$sb.AppendLine("    '$JMENDOZA',")
[void]$sb.AppendLine("    '2024-11-01 12:00:00+00'")
[void]$sb.AppendLine(");")
[void]$sb.AppendLine("")

# Members
[void]$sb.AppendLine("INSERT INTO group_members (id, group_id, user_id, role, joined_at) VALUES")
[void]$sb.AppendLine("    (gen_random_uuid(), '$GROUP_ID', '$JMENDOZA', 'Owner', '2024-11-01 12:00:00+00'),")
[void]$sb.AppendLine("    (gen_random_uuid(), '$GROUP_ID', '$JULIO',    'Member','2024-11-01 12:05:00+00');")
[void]$sb.AppendLine("")

# Expenses + splits
[void]$sb.AppendLine("INSERT INTO expenses")
[void]$sb.AppendLine("    (id, group_id, payer_user_id, created_by_user_id, description, amount_cents, idempotency_key, created_at)")
[void]$sb.AppendLine("VALUES")
$expLines = $expenses | ForEach-Object {
    $e = $_
    "    ('$($e.id)','$GROUP_ID','$($e.payer)','$($e.created_by)','$($e.desc -replace "'","''")',$($e.amount),'$($e.ikey)','$($e.ts)')"
}
[void]$sb.AppendLine(($expLines -join ",`n") + ";")
[void]$sb.AppendLine("")

[void]$sb.AppendLine("INSERT INTO expense_splits")
[void]$sb.AppendLine("    (id, expense_id, user_id, amount_owed_cents)")
[void]$sb.AppendLine("VALUES")
$splitLines = $expenses | ForEach-Object {
    $e = $_
    $s1 = "    (gen_random_uuid(),'$($e.id)','$($e.payer)',$($e.half_payer))"
    $s2 = "    (gen_random_uuid(),'$($e.id)','$($e.other)',$($e.half_other))"
    "$s1,`n$s2"
}
[void]$sb.AppendLine(($splitLines -join ",`n") + ";")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("COMMIT;")

$outPath = Join-Path $PSScriptRoot 'seed-ram-group.sql'
$sb.ToString() | Set-Content -Encoding UTF8 -Path $outPath
Write-Host "Seed SQL written to: $outPath"
Write-Host "Group ID: $GROUP_ID"
Write-Host ""
Write-Host "To apply:"
Write-Host "  docker exec -i mambasplit_db psql -U mambasplit -d mambasplit < .\scripts\seed-ram-group.sql"
