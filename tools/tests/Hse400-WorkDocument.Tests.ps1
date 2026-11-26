# tools/tests/Hse400-WorkDocument.Tests.ps1
# b14 document-move T2 (D-577, огляд H6): будівник HSE400 не створює ДРУГОГО робочого документа року - Land узяв би
# найбільший id як джерело складу (B80). Після перенесення 26.11 робочий документ - doc 98 у LANDUI2026.
# Мутація (локально, 26.11): у Resolve-Hse400WorkDocument прибрати гілку «документ року в іншому проєкті» - червоний
# третій тест; поміняти GOLD-виняток на порожній - червоний четвертий.
Describe 'HSE400 work document of a year' {
    BeforeAll {
        $root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $root 'tools/hse400/Hse400Project.Lib.ps1')
        function Get-Rejection([scriptblock]$Block) {
            try { & $Block; return $null } catch { return $_.Exception.Message }
        }
    }

    It 'документ у LANDUI2026 - він і є робочий' {
        $docs = @(@{ Id = 98; ProjectCode = 'LANDUI2026'; Year = 2026 }, @{ Id = 97; ProjectCode = 'HSE400_GOLD'; Year = 2026 })
        $id = Resolve-Hse400WorkDocument -ProjectCode 'LANDUI2026' -Year 2026 -Documents $docs
        if ($id -ne 98) { throw "очікувався 98, отримано $id" }
    }

    It 'року без документа - можна створити (null)' {
        $id = Resolve-Hse400WorkDocument -ProjectCode 'HSE400_2027' -Year 2027 -Documents @(@{ Id = 98; ProjectCode = 'LANDUI2026'; Year = 2026 })
        if ($null -ne $id) { throw "очікувався null, отримано $id" }
    }

    It 'документ року вже в іншому проєкті - відмова з його id і проєктом (не HSE400_2026 знову)' {
        $docs = @(@{ Id = 98; ProjectCode = 'LANDUI2026'; Year = 2026 })
        $msg = Get-Rejection { Resolve-Hse400WorkDocument -ProjectCode 'HSE400_2026' -Year 2026 -Documents $docs }
        if ($null -eq $msg) { throw 'другий робочий документ року прийнято' }
        if ($msg -notlike '*98*' -or $msg -notlike '*LANDUI2026*') { throw "відмова не про те: $msg" }
    }

    It 'золотий проєкт не рахується робочим' {
        $docs = @(@{ Id = 97; ProjectCode = 'HSE400_GOLD'; Year = 2026 })
        $id = Resolve-Hse400WorkDocument -ProjectCode 'LANDUI2026' -Year 2026 -Documents $docs
        if ($null -ne $id) { throw "золотий документ узято як робочий: $id" }
    }

    It 'два документи року в одному проєкті - відмова' {
        $docs = @(@{ Id = 98; ProjectCode = 'LANDUI2026'; Year = 2026 }, @{ Id = 120; ProjectCode = 'LANDUI2026'; Year = 2026 })
        $msg = Get-Rejection { Resolve-Hse400WorkDocument -ProjectCode 'LANDUI2026' -Year 2026 -Documents $docs }
        if ($null -eq $msg) { throw 'два робочі документи прийнято' }
    }

    It 'новий проєкт будівник створює лише зі старим кодом HSE400_<рік>' {
        if (-not (Test-Hse400MayCreateProject -ProjectCode 'HSE400_2026' -Year 2026)) { throw 'HSE400_2026 мав бути дозволений (scratch)' }
        if (Test-Hse400MayCreateProject -ProjectCode 'LANDUI2026' -Year 2026) { throw 'LANDUI2026 будівник не створює' }
    }

    It 'конструктор шукає робочий документ через Resolve-Hse400WorkDocument і не створює проєкт без дозволу' {
        $src = [IO.File]::ReadAllText((Join-Path $root 'tools/hse400/Build-Hse400Template.ps1'), [Text.Encoding]::UTF8)
        if ($src -notmatch 'Resolve-Hse400WorkDocument') { throw 'Build-Hse400Template.ps1 не викликає Resolve-Hse400WorkDocument' }
        if ($src -notmatch 'Test-Hse400MayCreateProject') { throw 'Build-Hse400Template.ps1 не перевіряє дозвіл створити проєкт' }
        if ($src -notmatch "\[string\]\`$ProjectCode = 'LANDUI2026'") { throw 'типовий -ProjectCode має бути LANDUI2026' }
    }
    It 'огляд M-2: рік робочого документа - з періодів проєкту (ProjectSummary року не має), і будівник викликає саме це' {
        # Справжні форми: документи { id, projectId }, проєкти { id, code } БЕЗ year, періоди { periods: [{ periodKey }] }.
        $docs = @([pscustomobject]@{ id = 98; projectId = 1 }, [pscustomobject]@{ id = 97; projectId = 6 })
        $projects = @([pscustomobject]@{ id = 1; code = 'LANDUI2026' }, [pscustomobject]@{ id = 6; code = 'HSE400_GOLD' })
        $calls = @{ n = 0 }
        $periods = { param($id) $calls.n++; [pscustomobject]@{ periods = @(@{ periodKey = 202603 }, @{ periodKey = 202601 }) } }
        $known = Get-Hse400YearDocuments -Documents $docs -Projects $projects -PeriodsOf $periods
        if (@($known | Where-Object { $_.Id -eq 98 -and $_.Year -eq 2026 -and $_.ProjectCode -eq 'LANDUI2026' }).Count -ne 1) { throw "рік/проєкт doc 98: $($known | Out-String)" }
        $msg = Get-Rejection { Resolve-Hse400WorkDocument -ProjectCode 'HSE400_2026' -Year 2026 -Documents $known }
        if ($null -eq $msg) { throw 'з роком із періодів другий робочий документ мав бути відхилений' }
        if ($calls.n -ne 2) { throw "періоди мали читатися раз на проєкт, а не $($calls.n)" }
        $src = [IO.File]::ReadAllText((Join-Path $root 'tools/hse400/Build-Hse400Template.ps1'), [Text.Encoding]::UTF8)
        if ($src -notmatch 'Get-Hse400YearDocuments') { throw 'будівник не бере рік з періодів' }
        if ($src -match '\$pr\.year') { throw 'будівник досі читає неіснуюче ProjectSummary.year' }
    }
}