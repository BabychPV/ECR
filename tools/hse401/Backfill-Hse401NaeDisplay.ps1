<#
.SYNOPSIS
    HSE401: показ наявних вузлів SRC дерева NAE_TREE як "<MPE> — <назва>" (або просто "<MPE>", коли назва = MPE).
    Колонка H_NAE шаблону - Lookup на NAE_TREE, сітка показує entry.display; у частини вузлів display = назва джерела ("ATC&DC"), а не номер MPE.

.DESCRIPTION
    Лише вузли SRC, ЧИННІ сьогодні (GET entries?level=SRC&asOf=сьогодні віддає тільки їх): вилучені вузли (validTo <= сьогодні, IsActive=false) у переліку
    відсутні й не чіпаються; додатково пропускається вузол, у деталях якого validTo <= сьогодні.
    MPE = точний текст колонки L Excel (регістр джерела) із витягу книги (out/hse401-extract.json) за кодом вузла N_<РЕГІОН>_<СУША/МОРЕ>_<MPE>;
    якщо вузла у витягу немає - хвіст коду (верхній регістр), такі вузли рахуються окремо ("запасний хвіст").
    Перейменування: POST /api/v1/registries/NAE_TREE/entries з Id наявного вузла (UpsertRegistryEntryHandler: назва ЗЛИВАЄТЬСЯ по мовах,
    новий часовий рядок не створюється, ревізія й аудит пишуться). Надсилаємо id, registryDefId, code (як є), levelCode (як є), parentEntryId
    (ОБОВ'ЯЗКОВО як є: у ієрархічному довіднику запит без батька переніс би вузол у корінь), display.values лише зі зміненими мовами, values = {}.
    Ідемпотентно: другий запуск - 0 змін. Версіонування: окремої версії немає (запис довідника, не шаблон) - кожна зміна = ревізія запису + подія аудиту.

.PARAMETER PreviewOnly  лише GET: лічильники (до зміни / вже гаразд / пропущено / запасний хвіст), перші приклади; нічого не пишеться.
#>
[CmdletBinding()]
param([string]$ExtractFile,
    [ValidateSet('Scratch', 'Live')][string]$Target = 'Scratch', [string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile,
    [switch]$PreviewOnly, [switch]$AcceptNaePreview)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Nae.Lib.ps1')
if ($MyInvocation.InvocationName -eq '.') { return }   # підключення як бібліотеки

Set-Hse401Target -Target $Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly -AcceptNaePreview:$AcceptNaePreview
$preview = Test-Hse401Preview
$out = Get-Hse401OutDir
if (-not $ExtractFile) { $ExtractFile = Join-Path $out 'hse401-extract.json' }
if (-not (Test-Path -LiteralPath $ExtractFile)) { throw "Немає витягу книги $ExtractFile - спершу Extract-Hse401.ps1." }

$sources = Get-Hse401NaeSources (Read-HseJson $ExtractFile)
$mpeByCode = @{}
foreach ($s in $sources.Values) { $mpeByCode[(Get-Hse401NaeSrcCode $s.region $s.onoff $s.mpe)] = [string]$s.mpe }
Write-HseLog "Джерел у витягу: $($sources.Count)"

Connect-Hse
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
$defId = $null
foreach ($r in @((Invoke-HseOk GET '/api/v1/registries').Json)) { if ([string]$r.code -eq 'NAE_TREE') { $defId = [int]$r.id } }
if ($null -eq $defId) { throw 'Довідника NAE_TREE немає.' }
$list = @(Get-HseItems (Invoke-HseOk GET "/api/v1/registries/NAE_TREE/entries?level=SRC&asOf=$today").Json)
Write-HseLog "NAE_TREE: чинних вузлів SRC: $($list.Count)"

$todo = New-Object System.Collections.Generic.List[object]
$ok = 0; $fallback = 0; $skipped = 0; $done = 0; $errs = New-Object System.Collections.Generic.List[string]
foreach ($e in $list) {
    $code = [string]$e.code
    $key = $code.ToUpperInvariant()
    $mpe = $null; $fromTail = $false
    if ($mpeByCode.ContainsKey($key)) { $mpe = $mpeByCode[$key] }
    else { $mpe = Get-Hse401MpeFromNaeCode $key; $fromTail = $true }
    if ([string]::IsNullOrWhiteSpace($mpe)) { $skipped++; continue }
    $d = (Invoke-HseOk GET "/api/v1/registries/NAE_TREE/entries/$($e.id)").Json
    if ($null -ne $d.PSObject.Properties['validTo'] -and $d.validTo -and ([datetime]$d.validTo).Date -le (Get-Date).Date) { $skipped++; continue }
    if ($null -ne $d.PSObject.Properties['levelCode'] -and $d.levelCode -and [string]$d.levelCode -ne 'SRC') { $skipped++; continue }
    $changed = [ordered]@{}
    $cur = $d.displayL10n.values
    foreach ($lang in @('en', 'ru')) {
        $p = $cur.PSObject.Properties[$lang]
        if ($null -eq $p -or [string]::IsNullOrWhiteSpace([string]$p.Value)) { continue }
        $want = Format-Hse401NaeDisplay -Mpe $mpe -Name ([string]$p.Value)
        if ($want -cne ([string]$p.Value).Trim()) { $changed[$lang] = $want }
    }
    if ($changed.Count -eq 0) { $ok++; continue }
    if ($fromTail) { $fallback++ }
    $todo.Add([ordered]@{ id = [long]$e.id; code = $code; mpe = $mpe; from = [string]$cur.en; display = $changed })
    if ($preview) { continue }
    $parent = $null; if ($null -ne $d.PSObject.Properties['parentEntryId']) { $parent = $d.parentEntryId }
    $body = @{ id = [long]$e.id; registryDefId = $defId; code = $code; display = @{ values = $changed }; parentEntryId = $parent; values = @{} }
    if ($null -ne $d.PSObject.Properties['levelCode'] -and $d.levelCode) { $body.levelCode = [string]$d.levelCode }
    $r = Invoke-Hse POST '/api/v1/registries/NAE_TREE/entries' $body
    if ($r.Status -in 200, 201) { $done++ } else { $errs.Add("$code -> $($r.Status) $(Get-EcrErrorCode $r)") }
}
Write-HseLog ("SRC: уже у форматі {0}; до зміни {1} (із них за запасним хвостом коду {2}); пропущено {3}" -f $ok, $todo.Count, $fallback, $skipped)
foreach ($x in ($todo | Select-Object -First 8)) { Write-HseLog ("  {0}: [{1}] -> [{2}]" -f $x.code, $x.from, $x.display['en']) }
if ($preview) { Write-HseLog '[preview] запису не було.'; return }
Write-HseLog "оновлено: $done; помилок: $($errs.Count)"
foreach ($m in ($errs | Select-Object -First 10)) { Write-HseLog "  ПОМИЛКА $m" }
if ($errs.Count -gt 0) { exit 2 }
