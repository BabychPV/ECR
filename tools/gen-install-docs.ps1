<#
.SYNOPSIS
    Перегенеровує скрипт прав облікового запису служби в інструкціях встановлення (DB-1).

.DESCRIPTION
    Джерело — перелік прав ServiceAccountGrants (tests/Ecr.TestKit/ServiceAccountGrants.cs). Блоки між маркерами
    <!-- generated:grants --> … <!-- /generated:grants --> у docs/admin/operations-runbook.md,
    docs/build/11-install-guide.md, docs/delivery/QUICKSTART-FIRST-TEST.md (і в будь-якому docs/**/*.md із маркером)
    переписує сторож ServiceAccountGrantsRunbookTests у режимі ECR_REGENERATE_DOCS=1. Без змінної (CI) той самий
    тест лише звіряє і червоніє з цією командою. Руками блоки не правити.

.EXAMPLE
    powershell -File tools/gen-install-docs.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests\Ecr.Architecture.Tests\Ecr.Architecture.Tests.csproj'

$previous = $env:ECR_REGENERATE_DOCS
$env:ECR_REGENERATE_DOCS = '1'
try {
    & dotnet test $project --filter 'FullyQualifiedName~ServiceAccountGrantsRunbookTests' --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw "gen-install-docs: генерація не пройшла (код $LASTEXITCODE) — дивіться вивід тесту вище."
    }
}
finally {
    $env:ECR_REGENERATE_DOCS = $previous
}

Write-Host 'gen-install-docs: скрипт прав у інструкціях встановлення оновлено з ServiceAccountGrants.'
