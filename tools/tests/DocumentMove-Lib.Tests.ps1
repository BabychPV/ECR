# tools/tests/DocumentMove-Lib.Tests.ps1
# b14 document-move T1 (D-579): складання SQL перенесення в DocumentMove.Lib.ps1 - запобіжники бази, CorrelationId,
# підстановка блоку параметрів і тексту Q-17 (Apply, системне перенесення в закриті місяці, жетон обгортки).
# Справжній прогін SQL - tests/Ecr.Infrastructure.Tests/Persistence/DocumentMoveSqlTests.cs (ця сама бібліотека).
Describe 'DocumentMove library' {
    BeforeAll {
        $root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $root 'tools/document-move/DocumentMove.Lib.ps1')
        $moveSql = [IO.File]::ReadAllText((Join-Path $root 'tools/document-move/Move-Document.sql'))
        $q17Sql = [IO.File]::ReadAllText((Join-Path $root 'tools/q17/Preview-MonthColumnConsolidation.sql'))
        function Get-Rejection([scriptblock]$Block) {
            try { & $Block; return $null } catch { return $_.Exception.Message }
        }
    }

    It 'бази старої системи й чужі - відмова' {
        foreach ($db in 'ECR', 'flert', 'EcrLegacyProd') {
            if ($null -eq (Get-Rejection { Assert-MoveDatabase $db })) { throw "базу $db прийнято" }
        }
        Assert-MoveDatabase 'EcrLandB14Reh'
    }

    It 'CorrelationId називає бокову базу pre-image і не пропускає зайвих символів' {
        if ((Get-PreImageDatabaseName 'b14move-1') -ne 'EcrMovePre_b14move-1') { throw 'ім''я бази pre-image' }
        foreach ($bad in 'short', "b14'; DROP", 'a b c d e f') {
            if ($null -eq (Get-Rejection { Assert-CorrelationId $bad })) { throw "CorrelationId '$bad' прийнято" }
        }
    }

    It 'скрипт перенесення: параметри підставлено, Q-17 вставлено в режимі Apply із записом у закриті місяці' {
        $text = New-MoveScript -MoveSql $moveSql -Q17Sql $q17Sql -Mode Preview -DocumentId 98 -TargetProjectCode 'LANDUI2026' `
            -CorrelationId 'b14move-1' -Reason "human 25.11: one project, O'Neil" -UserId 3
        if ($text -notmatch "@MvMode\s+varchar\(10\)\s+= 'Preview'") { throw 'режим' }
        if ($text -notmatch '@MvDocumentId\s+bigint\s+= 98') { throw 'документ' }
        if ($text -notmatch "@MvReason\s+nvarchar\(1000\) = N'human 25.11: one project, O''Neil'") { throw 'причина з лапкою' }
        if ($text -notmatch "@MvApplyToken\s+varchar\(40\)\s+= 'MOVE-APPLY-v1'") { throw 'жетон обгортки' }
        if ($text -notmatch '@MvAllowLive\s+bit\s+= 0') { throw 'AllowLive без ключа' }
        if ($text -match '-- @@Q17@@') { throw 'місце Q-17 не замінено' }
        if ($text -notmatch "DECLARE @Mode\s+varchar\(10\)\s+= 'Apply'") { throw 'Q-17 не в Apply' }
        if ($text -notmatch 'DECLARE @WriteLocked bit\s+= 1') { throw 'Q-17 без запису в закриті місяці' }
        if ($text -notmatch 'DECLARE @DocumentId\s+bigint\s+= 98') { throw 'Q-17 не для цього документа' }
        if ($text -notmatch "DECLARE @RunId\s+nvarchar\(64\) = N'b14move-1'") { throw 'Q-17 без CorrelationId' }
        if (([regex]::Matches($text, '-- BEGIN PARAMS')).Count -ne 0) { throw 'лишився маркер блоку параметрів' }
    }

    It '-AllowLive передається лише явно' {
        $text = New-MoveScript -MoveSql $moveSql -Q17Sql $q17Sql -Mode Apply -DocumentId 93 -TargetProjectCode 'LANDUI2026' `
            -CorrelationId 'b14move-1' -Reason 'b14: one project LANDUI2026' -UserId 3 -AllowLive
        if ($text -notmatch '@MvAllowLive\s+bit\s+= 1') { throw '-AllowLive не передано' }
    }

    It 'Preview відкочується самим SQL, а не ключем обгортки' {
        if ($moveSql -notmatch "IF @MvMode = 'Apply' COMMIT; ELSE ROLLBACK;") { throw 'Move-Document.sql: Preview має завжди відкочуватись' }
        $reverse = [IO.File]::ReadAllText((Join-Path $root 'tools/document-move/Reverse-Move.sql'))
        if ($reverse -notmatch "IF @RvMode = 'Apply' COMMIT; ELSE ROLLBACK;") { throw 'Reverse-Move.sql: Preview має завжди відкочуватись' }
    }

    It 'SQL - лише ASCII (sqlcmd / PowerShell 5.1 без BOM)' {
        foreach ($f in 'Move-Document.sql', 'Export-PreImage.sql', 'Reverse-Move.sql') {
            $bytes = [IO.File]::ReadAllBytes((Join-Path $root "tools/document-move/$f"))
            if (@($bytes | Where-Object { $_ -gt 127 }).Count -gt 0) { throw "$f містить не-ASCII" }
        }
    }
    It 'огляд L-1/51804: запис у live EcrLandUi без -AllowLive відмовляє ДО підключення' {
        $script = Join-Path $root 'tools/document-move/Move-Document.ps1'
        $msg = Get-Rejection { & $script -Server 'no-such-server-b14' -Database 'EcrLandUi' -Mode Apply -Documents 98 -CorrelationId 'b14move-1' -UserId 3 -Reason 'b14: one project LANDUI2026' }
        if ($msg -notlike '*AllowLive*') { throw "очікувалась відмова -AllowLive до підключення: $msg" }
    }

    It 'огляд M-1: Apply передає pre-image і тайм-аут замків у SQL' {
        $text = New-MoveScript -MoveSql $moveSql -Q17Sql $q17Sql -Mode Apply -DocumentId 98 -TargetProjectCode 'LANDUI2026' `
            -CorrelationId 'b14move-1' -Reason 'b14: one project LANDUI2026' -UserId 3 -PreImage 'EcrMovePre_b14move-1' -LockTimeoutMs 500
        if ($text -notmatch "@MvPreImage\s+sysname\s+= N'EcrMovePre_b14move-1'") { throw 'pre-image не передано' }
        if ($text -notmatch '@MvLockTimeout\s+int\s+= 500') { throw 'тайм-аут замків не передано' }
        if ($moveSql -notmatch "ecr:doc-structure:" -or $moveSql -notmatch "ecr:sheet-edit:") { throw 'немає замків продукту (SheetEditGate)' }
    }
}