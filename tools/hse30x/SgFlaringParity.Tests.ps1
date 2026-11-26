# tools/hse30x/SgFlaringParity.Tests.ps1
# SGF-flaring AC-1 (review H4): self-test of the parity comparison on a synthetic dataset, no database.
# Run: Invoke-Pester tools/hse30x/SgFlaringParity.Tests.ps1   (Pester 3.4+ syntax: Should Be / Should BeNullOrEmpty)

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'SgFlaringParity.Lib.ps1')

function New-Row($Kind, $Key, $Column, $Value) { [pscustomobject]@{ Kind = $Kind; Key = $Key; Column = $Column; Value = $Value } }
function New-EcrRow($Kind, $RowKey, $EventId, $Column, $Value, $Substance = $null) {
    [pscustomobject]@{ Kind = $Kind; RowKey = $RowKey; EventId = $EventId; Column = $Column; Value = $Value; SubstanceEntryId = $Substance }
}

Describe 'SGF parity: CONT key EFID|FCID' {
    It 'builds the ECR CONT key from the event id and the SGF_FCID output of the same row' {
        $rows = @(
            (New-EcrRow 'CONT' '7:D1-S1' 'EF-1' 'SGF_FCID' 996),
            (New-EcrRow 'CONT' '7:D1-S1' 'EF-1' 'H2S_mass' 1.5),
            (New-EcrRow 'CONT' '7:D1-S2' 'EF-1' 'SGF_FCID' 1000),
            (New-EcrRow 'CONT' '7:D1-S2' 'EF-1' 'H2S_mass' 2.5))
        $ecr = @(ConvertTo-SgEcrRows -Rows $rows)
        @($ecr | Where-Object Column -eq 'SGF_FCID').Count | Should Be 0
        ($ecr | Where-Object Key -eq 'EF-1|996').Value | Should Be 1.5
        ($ecr | Where-Object Key -eq 'EF-1|1000').Value | Should Be 2.5
    }

    It 'matches legacy EFID|FCID rows and flags a different value' {
        $legacy = @((New-Row 'CONT' 'EF-1|996' 'H2S_mass' 1.5), (New-Row 'CONT' 'EF-1|1000' 'H2S_mass' 2.0))
        $ecr = @(ConvertTo-SgEcrRows -Rows @(
            (New-EcrRow 'CONT' 'a' 'EF-1' 'SGF_FCID' 996), (New-EcrRow 'CONT' 'a' 'EF-1' 'H2S_mass' 1.5),
            (New-EcrRow 'CONT' 'b' 'EF-1' 'SGF_FCID' 1000), (New-EcrRow 'CONT' 'b' 'EF-1' 'H2S_mass' 2.5)))
        $out = @(Compare-SgParity -Legacy $legacy -Ecr $ecr)
        ($out | Where-Object Key -eq 'EF-1|996').Status | Should Be 'equal'
        ($out | Where-Object Key -eq 'EF-1|1000').Status | Should Be 'different'
    }
}

Describe 'SGF parity: duplicates and substances' {
    It 'reports duplicate keys instead of overwriting' {
        $dups = @(Find-SgDuplicates -Rows @((New-Row 'INT' 'EF-9' 'X' 1), (New-Row 'INT' 'EF-9' 'X' 2)) -Side 'ECR')
        $dups.Count | Should Be 1
        $dups[0] | Should Be 'ECR duplicate: INT|EF-9|X x2'
    }

    It 'keeps the substance in the column so per-substance outputs do not collapse' {
        $ecr = @(ConvertTo-SgEcrRows -Rows @((New-EcrRow 'INT' 'r' 'EF-2' 'tonn' 1 101), (New-EcrRow 'INT' 'r' 'EF-2' 'tonn' 2 102)))
        @(Find-SgDuplicates -Rows $ecr -Side 'ECR').Count | Should Be 0
        ($ecr | Where-Object Column -eq 'tonn#102').Value | Should Be 2
    }
}

Describe 'SGF parity: period window' {
    It 'is [From, To) for monthly keys and takes a yearly key only when the whole year is inside' {
        $w = Get-SgPeriodWindow -From ([datetime]'2025-01-01') -To ([datetime]'2026-01-01')
        $w.FromKey | Should Be 202501
        $w.ToKey | Should Be 202601
        $w.Sql | Should Match 'r.PeriodKey < 202601'
        $w.Sql | Should Match 'r.PeriodKey \+ 12 < 202601'
    }
}

Describe 'SGF parity: tolerance and known classes' {
    It 'treats a relative difference below the tolerance as equal and classifies TOX Train 1/481 as L6' {
        $legacy = @((New-Row 'INT' 'EF-3' 'MW' 100.0), (New-Row 'TOX' '2026-03-01|Train 1|Normal|SOR|481' 'H2S' 5))
        $ecr = @((New-Row 'INT' 'EF-3' 'MW' 100.00000001), (New-Row 'TOX' '2026-03-01|Train 1|Normal|SOR|481' 'H2S' 6))
        $out = @(Compare-SgParity -Legacy $legacy -Ecr $ecr -RelTolerance 1e-6)
        ($out | Where-Object Kind -eq 'INT').Status | Should Be 'equal'
        ($out | Where-Object Kind -eq 'TOX').Known | Should Be 'L6'
    }
}
