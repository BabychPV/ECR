<#
.SYNOPSIS
    Швидкий контур lane-гілки: тести лише тих тестових КЛАСІВ, що змінені в diff.
.DESCRIPTION
    Використовується завданням `lane-affected` у `.github/workflows/ci.yml` на push у
    не-інтеграційну `lane/**`. Береться diff від merge-base з `origin/dev/integration`,
    у `tests/<Проєкт>.Tests/**/*.cs` вибираються оголошені класи, і для кожного проєкту
    запускається `dotnet test --filter "FullyQualifiedName~КласА|FullyQualifiedName~КласБ"`.

    ⚠ Це евристика, а не повний набір: зміна ЛИШЕ в `src/**` без правки тестів сюди не
    потрапляє (її ловить Evidence: усі тести `Category!=Integration`, і повний набір на
    `lane/integ-*` / `dev/integration` / PR). Повний набір — за `verify-all.ps1`.

.PARAMETER Base
    З чим порівнювати (типово `origin/dev/integration`).
.PARAMETER Plan
    Лише надрукувати план і записати `has_work=true|false` у `$GITHUB_OUTPUT`.
.PARAMETER Run
    Виконати план (`dotnet test` по проєктах); код виходу ≠ 0, якщо впав хоч один.
.PARAMETER Files
    Замість git diff — явний перелік змінених файлів (для локальної перевірки).
#>
[CmdletBinding()]
param(
    [string] $Base = 'origin/dev/integration',
    [switch] $Plan,
    [switch] $Run,
    [string[]] $Files
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

if ($Files) { $Files = @($Files | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }
if (-not $Files) {
    $mb = (& git merge-base $Base HEAD).Trim()
    if (-not $mb) { throw "Немає merge-base з $Base — fetch-depth: 0?" }
    $Files = @(& git diff --name-only --diff-filter=AMR $mb HEAD)
}

$classRx = [regex] '(?m)^\s*(?:(?:public|internal|private|protected)\s+)?(?:(?:sealed|static|abstract|partial|unsafe)\s+)*class\s+([A-Za-z_][A-Za-z0-9_]*)'
$byProject = [ordered] @{}

foreach ($f in $Files) {
    $norm = $f -replace '\\', '/'
    if (-not ($norm -match '^tests/([^/]+\.Tests)/.+\.cs$')) { continue }
    $project = $Matches[1]
    if ($norm -match '/(obj|bin)/') { continue }
    $path = Join-Path $root $norm
    if (-not (Test-Path -LiteralPath $path)) { continue }
    if (-not (Test-Path -LiteralPath (Join-Path $root "tests/$project/$project.csproj"))) { continue }

    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    foreach ($m in $classRx.Matches($text)) {
        if (-not $byProject.Contains($project)) { $byProject[$project] = [System.Collections.Generic.HashSet[string]]::new() }
        [void] $byProject[$project].Add($m.Groups[1].Value)
    }
}

$entries = foreach ($p in $byProject.Keys) {
    $classes = @($byProject[$p] | Sort-Object)
    [pscustomobject] @{
        Project = $p
        Classes = $classes
        Filter  = ($classes | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
    }
}
$entries = @($entries)

if ($entries.Count -eq 0) {
    Write-Host 'lane-affected: у diff немає змінених тестових класів — нічого запускати (Evidence покриває Category!=Integration).'
}
else {
    foreach ($e in $entries) {
        Write-Host ("lane-affected: {0} — {1} клас(ів): {2}" -f $e.Project, $e.Classes.Count, ($e.Classes -join ', '))
    }
}

if ($Plan) {
    if ($env:GITHUB_OUTPUT) {
        "has_work=$(if ($entries.Count -gt 0) { 'true' } else { 'false' })" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    }
    return
}

if (-not $Run) { return }

$failed = @()
foreach ($e in $entries) {
    Write-Host "::group::dotnet test $($e.Project)"
    & dotnet test (Join-Path $root "tests/$($e.Project)/$($e.Project).csproj") -c Release --nologo `
        --logger 'console;verbosity=normal' --filter $e.Filter
    $code = $LASTEXITCODE
    Write-Host '::endgroup::'
    # 8 = «жоден тест не відповідає фільтру» у `dotnet test` не завжди; нульовий збіг класу
    # (клас без тестів — фікстура, база) — не помилка, але помилка збірки/тесту — так.
    if ($code -ne 0) { $failed += "$($e.Project) (код $code)" }
}

if ($failed.Count -gt 0) {
    Write-Host "::error::lane-affected: впали проєкти: $($failed -join '; ')"
    exit 1
}
exit 0
