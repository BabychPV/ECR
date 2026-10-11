<#
.SYNOPSIS
    Розбір xlsm Land -> модель Excel (JSON) -> модель побудови шаблону ECR (land-template.model.json).

.DESCRIPTION
    Python 3.12 + openpyxl (pip install openpyxl). Нічого не пише в репозиторій, окрім -OutModel.
    Кроки: xlload.py (xlsm -> pickle), excel_model.py (аркуші/таблиці/колонки/DV/CF/формули), gen_ecr_model.py
    (довідники, колонки ECR, рядки Fixed, правила валідації, мостові формули, виходи методологій).
    compare_fe_be.py порівнює Front end і Back end (потрібні обидва xlsm).

.EXAMPLE
    .\run-model-gen.ps1 -Front "docs\source\ECR Current Front end\RDS_xxx_Template_Land_2026_v1.0.4.0 2.xlsm" `
        -Back "docs\source\ECR Current Back end\Excel\RDS_xxx_Template_Land_2026_v1.0.4.0.xlsm"
#>
param(
    [Parameter(Mandatory)][string]$Front,
    [string]$Back,
    [string]$AfPackage,
    [string]$OutModel,
    [string]$Work = (Join-Path $env:TEMP 'land-model-gen')
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..\..')).Path
$refDir = Join-Path $repo 'docs\delivery\reference-data\land-template'
if (-not $AfPackage) { $AfPackage = Join-Path $refDir 'af-land-package.json' }
if (-not $OutModel) { $OutModel = Join-Path $refDir 'land-template.model.json' }
New-Item -ItemType Directory -Force $Work | Out-Null
Copy-Item -LiteralPath $AfPackage (Join-Path $Work 'af-land-package.json') -Force
$env:PYTHONIOENCODING = 'utf-8'
Push-Location $Work
try {
    py -3.12 (Join-Path $here 'xlload.py') (Resolve-Path -LiteralPath $Front).Path 'fe.pkl'
    py -3.12 (Join-Path $here 'excel_model.py') fe model_fe.json
    if ($Back) {
        py -3.12 (Join-Path $here 'xlload.py') (Resolve-Path -LiteralPath $Back).Path 'be.pkl'
        py -3.12 (Join-Path $here 'excel_model.py') be model_be.json
        py -3.12 (Join-Path $here 'compare_fe_be.py')
    }
    py -3.12 (Join-Path $here 'gen_ecr_model.py') $OutModel
    Write-Host "Модель: $OutModel"
}
finally { Pop-Location }