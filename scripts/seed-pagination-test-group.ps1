#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generates SQL for a local pagination test group.

    Shape:
      - 200 recent unsettled expenses
      - 7 settlements
      - 4 settlements with more than 25 linked expenses
      - 3 settlements with fewer than 25 linked expenses

    Run from repo root:
        .\scripts\seed-pagination-test-group.ps1

    Apply to local Docker Postgres:
        docker exec -i mambasplit_db psql -U mambasplit -d mambasplit < .\scripts\seed-pagination-test-group.sql
#>

$ErrorActionPreference = 'Stop'

$JMENDOZA = 'c8aa8a33-b533-4def-8985-6860915071f1'
$JULIO = 'dc1c5a11-fc7b-4a79-a970-82dabf159581'
$GROUP_ID = 'cb3d0a2d-99ef-4f3e-96c2-31f919111057'
$GROUP_NAME = 'Pagination Test Group'
$BASE_DATE = [datetime]::new(2026, 5, 8, 18, 0, 0, [datetimekind]::Utc)
$rng = [System.Random]::new(31057)

$catalogue = @(
    @{ desc = 'Coffee'; min = 650; max = 2200 },
    @{ desc = 'Lunch'; min = 1200; max = 4800 },
    @{ desc = 'Groceries'; min = 3600; max = 15500 },
    @{ desc = 'Gas'; min = 2800; max = 7600 },
    @{ desc = 'Target'; min = 1800; max = 9500 },
    @{ desc = 'Pharmacy'; min = 900; max = 6200 },
    @{ desc = 'Dinner'; min = 3200; max = 12000 },
    @{ desc = 'Utilities'; min = 6400; max = 19000 },
    @{ desc = 'Household'; min = 1500; max = 8500 },
    @{ desc = 'Subscription'; min = 1100; max = 3200 }
)

$settlementPlans = @(
    @{ label = 'Settlement batch 1'; count = 30; daysAgo = 14 },
    @{ label = 'Settlement batch 2'; count = 28; daysAgo = 20 },
    @{ label = 'Settlement batch 3'; count = 27; daysAgo = 26 },
    @{ label = 'Settlement batch 4'; count = 26; daysAgo = 32 },
    @{ label = 'Settlement batch 5'; count = 24; daysAgo = 38 },
    @{ label = 'Settlement batch 6'; count = 12; daysAgo = 44 },
    @{ label = 'Settlement batch 7'; count = 8; daysAgo = 50 }
)

function SqlString {
    param([string]$Value)
    return $Value.Replace("'", "''")
}

function Timestamp {
    param([datetime]$Value)
    return $Value.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss') + '+00'
}

function New-Expense {
    param(
        [string]$Description,
        [datetime]$CreatedAt,
        [int]$Index,
        [bool]$Settled,
        [string]$ForcedPayer = ''
    )

    $cat = $catalogue[$rng.Next(0, $catalogue.Count)]
    $amount = $rng.Next($cat.min, $cat.max)
    $payer = if ($ForcedPayer) { $ForcedPayer } elseif ($rng.NextDouble() -lt 0.55) { $JMENDOZA } else { $JULIO }
    $other = if ($payer -eq $JMENDOZA) { $JULIO } else { $JMENDOZA }
    $payerShare = [math]::Floor($amount / 2)
    $otherShare = $amount - $payerShare
    $prefix = if ($Settled) { 'settled' } else { 'unsettled' }
    $id = [guid]::NewGuid().ToString()

    return @{
        id = $id
        payer = $payer
        other = $other
        description = "$Description $($cat.desc)"
        amount = $amount
        payerShare = $payerShare
        otherShare = $otherShare
        createdAt = Timestamp $CreatedAt
        idempotencyKey = "seed-pagination-$prefix-$Index-$($id.Substring(0, 8))"
    }
}

$unsettledExpenses = [System.Collections.Generic.List[hashtable]]::new()
for ($i = 0; $i -lt 200; $i++) {
    $createdAt = $BASE_DATE.AddHours(-1 * $i)
    $unsettledExpenses.Add((New-Expense "Recent unsettled $($i + 1)" $createdAt $i $false))
}

$settlements = [System.Collections.Generic.List[hashtable]]::new()
$settledExpenses = [System.Collections.Generic.List[hashtable]]::new()
$settledIndex = 0

for ($s = 0; $s -lt $settlementPlans.Count; $s++) {
    $plan = $settlementPlans[$s]
    $settlementId = [guid]::NewGuid().ToString()
    $createdAt = $BASE_DATE.AddDays(-1 * $plan['daysAgo'])
    $linkedExpenses = [System.Collections.Generic.List[hashtable]]::new()

    for ($i = 0; $i -lt $plan['count']; $i++) {
        $expenseCreatedAt = $createdAt.AddHours(-2).AddMinutes(-20 * $i)
        $expense = New-Expense "$($plan['label']) expense $($i + 1)" $expenseCreatedAt $settledIndex $true $JMENDOZA
        $linkedExpenses.Add($expense)
        $settledExpenses.Add($expense)
        $settledIndex++
    }

    $amountCents = 0
    foreach ($linkedExpense in $linkedExpenses) {
        $amountCents += $linkedExpense.otherShare
    }
    $settlements.Add(@{
        id = $settlementId
        fromUserId = $JULIO
        toUserId = $JMENDOZA
        amountCents = $amountCents
        note = "$($plan['label']): $($plan['count']) linked expenses"
        createdAt = Timestamp $createdAt
        expenses = $linkedExpenses
    })
}

$allExpenses = @($unsettledExpenses) + @($settledExpenses)
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("-- Pagination Test Group seed")
[void]$sb.AppendLine("-- Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("-- Group ID: $GROUP_ID")
[void]$sb.AppendLine("-- 200 unsettled expenses; settlement linked counts: 30, 28, 27, 26, 24, 12, 8")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("BEGIN;")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("-- Recreate only this fixture group so reruns do not duplicate data.")
[void]$sb.AppendLine("DELETE FROM groups WHERE id = '$GROUP_ID';")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("INSERT INTO groups (id, name, created_by, created_at) VALUES")
[void]$sb.AppendLine("    ('$GROUP_ID', '$GROUP_NAME', '$JMENDOZA', '$(Timestamp $BASE_DATE)');")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("INSERT INTO group_members (id, group_id, user_id, role, joined_at) VALUES")
[void]$sb.AppendLine("    (gen_random_uuid(), '$GROUP_ID', '$JMENDOZA', 'OWNER', '$(Timestamp $BASE_DATE)'),")
[void]$sb.AppendLine("    (gen_random_uuid(), '$GROUP_ID', '$JULIO', 'MEMBER', '$(Timestamp $BASE_DATE.AddMinutes(5))');")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("INSERT INTO expenses")
[void]$sb.AppendLine("    (id, group_id, payer_user_id, created_by_user_id, description, amount_cents, reversal_of_expense_id, idempotency_key, idempotency_hash, created_at)")
[void]$sb.AppendLine("VALUES")
$expenseLines = $allExpenses | ForEach-Object {
    "    ('$($_.id)', '$GROUP_ID', '$($_.payer)', '$($_.payer)', '$(SqlString $_.description)', $($_.amount), NULL, '$($_.idempotencyKey)', NULL, '$($_.createdAt)')"
}
[void]$sb.AppendLine(($expenseLines -join ",`n") + ";")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("INSERT INTO expense_splits (id, expense_id, user_id, amount_owed_cents)")
[void]$sb.AppendLine("VALUES")
$splitLines = $allExpenses | ForEach-Object {
    "    (gen_random_uuid(), '$($_.id)', '$($_.payer)', $($_.payerShare)),`n    (gen_random_uuid(), '$($_.id)', '$($_.other)', $($_.otherShare))"
}
[void]$sb.AppendLine(($splitLines -join ",`n") + ";")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("INSERT INTO settlements (id, group_id, from_user_id, to_user_id, amount_cents, note, created_at)")
[void]$sb.AppendLine("VALUES")
$settlementLines = $settlements | ForEach-Object {
    "    ('$($_.id)', '$GROUP_ID', '$($_.fromUserId)', '$($_.toUserId)', $($_.amountCents), '$(SqlString $_.note)', '$($_.createdAt)')"
}
[void]$sb.AppendLine(($settlementLines -join ",`n") + ";")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("INSERT INTO settlement_expenses (id, settlement_id, expense_id)")
[void]$sb.AppendLine("VALUES")
$linkLines = foreach ($settlement in $settlements) {
    foreach ($expense in $settlement.expenses) {
        "    (gen_random_uuid(), '$($settlement.id)', '$($expense.id)')"
    }
}
[void]$sb.AppendLine(($linkLines -join ",`n") + ";")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("COMMIT;")

$outPath = Join-Path $PSScriptRoot 'seed-pagination-test-group.sql'
$sb.ToString() | Set-Content -Encoding UTF8 -Path $outPath

Write-Host "Seed SQL written to: $outPath"
Write-Host "Group ID: $GROUP_ID"
Write-Host "Group name: $GROUP_NAME"
Write-Host "Unsettled expenses: $($unsettledExpenses.Count)"
Write-Host "Settlements: $($settlements.Count)"
Write-Host "Settlement linked expense counts: $((($settlementPlans | ForEach-Object { $_['count'] }) -join ', '))"
