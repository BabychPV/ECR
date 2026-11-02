<#
.SYNOPSIS
    Splits the single seed script 09-seed.sql into seed/00-prelude.sql,
    seed/features/<AREA>-<slug>.sql and seed/99-epilogue.sql (deterministic,
    byte-preserving), or verifies an existing split against the original.

.DESCRIPTION
    Why: every lane appended its ru/kz portion right before the final
    `MERGE sys_ecr.UiString` of one 27k-line file, so parallel lanes conflicted
    on the same lines. After the split a lane adds ITS OWN file under
    seed/features/ and touches nothing shared.

    Run order (SeedRunner, one transaction, one connection):
        00-prelude.sql -> features/*.sql (StringComparer.Ordinal by name) -> 99-epilogue.sql

    CLASSIFICATION (what moves and why):
      * MOVABLE = a named section that lies between the batch that creates
        `#I18N` and the final `MERGE sys_ecr.UiString ... FROM #I18N`, starts
        right after a `GO` (only comments/blank lines in between), ends with
        `GO` + its own end marker `-- <KEY> <box><box> <"end of section" in Ukrainian> <box><box>`, and whose
        every batch is exactly `INSERT INTO #I18N ([Key], Lang, Val) ...
        OPTION (RECOMPILE);` + `GO`. Such sections only add rows to a temp
        table with PRIMARY KEY ([Key], Lang); inserts into it commute, nothing
        reads `#I18N` before the epilogue MERGE, so their relative order (and
        their order against the unnamed `#I18N` portions that stay in the
        prelude) cannot change the result. -> seed/features/<AREA>-<slug>.sql.
      * PRELUDE (stays in place, original order) = everything before the
        `#I18N` region: SET options, languages, permission catalog (and the
        cleanup DELETEs before it), dimensions/units and the unit sections
        (HSE301:F1, UNITS:ecr-derived, HSE301:NM3, LAND:units - later UPDATEs
        read earlier rows), password policy, svc-integration user, roles and
        role grants (SEC:RPT, SEC:SUBMIT, PERMITS:permissions - grants JOIN
        roles/permissions created above), period policy, UiStringRevision, the
        en batch (changed texts -> removed keys -> en MERGE with en subsections
        INSIDE its VALUES list -> Revision bump: one batch with shared
        variables, not self-contained per section). Plus the `#I18N` CREATE and
        every `#I18N` portion that is not a movable section (unnamed portions
        and sections with an unmatched/odd marker) - they stay where they were.
      * EPILOGUE (stays, original order) = from the `#I18N` MERGE to the end:
        the `#I18N` MERGE + Revision bump + DROP, rpt.ReportDef/ReportVersion,
        COLL:an14-ecr230 (needs the rpt rows above), COLL:period0-supersede,
        FP:sysadmin-rights, TC:discuss (read the final role grants).
      * SCAFFOLDING (the only text this script adds): the prelude ends with a
        batch that creates `#UiChanged` / `#UiRemoved`; the epilogue starts
        with a batch that applies them (same WHERE as the shared lists:
        only while the stored value is still the old default) and bumps
        Revision only if a row changed. Both tables are empty after the split,
        so the DB result is identical; new features add rows there in their
        own file instead of editing the shared lists in the prelude.

    -Verify proves: (a) every output file equals the planned bytes; (b) the
    planned segments (prelude pieces + features at their ORIGINAL positions +
    epilogue, scaffolding removed) concatenate to the original byte-for-byte;
    (c) the set of feature files on disk equals the planned set; (d) the
    run-order concatenation (what SeedRunner executes) has the same multiset
    of lines as the original plus the scaffolding lines.

    Encoding: works on the exact decoded text of the bytes (strict UTF-8),
    keeps line endings as they are, writes UTF-8 without BOM unless the
    original had one (then only the prelude carries it). Never uses
    Get-Content/Set-Content (they corrupt Cyrillic and add a BOM in PS 5.1).

.EXAMPLE
    powershell -NoProfile -File tools\split-seed.ps1                 # split + verify, deletes 09-seed.sql
    powershell -NoProfile -File tools\split-seed.ps1 -Source C:\x\09-seed.sql -Verify   # verify only
    # re-run on a newer main: git show origin/main:src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql > (Write via .NET!)
    # simpler: git checkout origin/main -- src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql ; then run without args.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [string]$OutDir,
    [switch]$Verify,
    [switch]$KeepSource,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }
$sqlDir = [IO.Path]::Combine($root, 'src', 'Ecr.Infrastructure', 'Persistence', 'Sql')
if (-not $Source) { $Source = [IO.Path]::Combine($sqlDir, '09-seed.sql') }
if (-not $OutDir) { $OutDir = [IO.Path]::Combine($sqlDir, 'seed') }
$Source = [IO.Path]::GetFullPath($Source)
$OutDir = [IO.Path]::GetFullPath($OutDir)
$featDir = [IO.Path]::Combine($OutDir, 'features')

# ---- non-ASCII constants built from code points (this file stays ASCII) ----
$BOX = [string][char]0x2500                                   # box drawing light horizontal
$KINETS = -join ([char[]](0x043A,0x0456,0x043D,0x0435,0x0446,0x044C))  # "kinets" (end)
$SEKTSII = -join ([char[]](0x0441,0x0435,0x043A,0x0446,0x0456,0x0457)) # "sektsii" (of section)
$END_WORDS = "$KINETS $SEKTSII"

$utf8Strict = New-Object Text.UTF8Encoding($false, $true)

function Read-Exact([string]$path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $offset = 0; if ($bom) { $offset = 3 }
    $text = $utf8Strict.GetString($bytes, $offset, $bytes.Length - $offset)
    $back = $utf8Strict.GetBytes($text)
    if ($back.Length -ne ($bytes.Length - $offset)) { throw "Round-trip of $path changed its length - refusing to touch it." }
    for ($i = 0; $i -lt $back.Length; $i++) { if ($back[$i] -ne $bytes[$i + $offset]) { throw "Round-trip of $path changed byte $i - refusing to touch it." } }
    return @{ Text = $text; Bom = $bom; Bytes = $bytes }
}

function Get-Bytes([string]$text, [bool]$bom) {
    $body = $utf8Strict.GetBytes($text)
    if (-not $bom) { return ,$body }
    $all = New-Object byte[] ($body.Length + 3)
    $all[0] = 0xEF; $all[1] = 0xBB; $all[2] = 0xBF
    [Array]::Copy($body, 0, $all, 3, $body.Length)
    return ,$all
}

function Split-Lines([string]$text) {
    # Lines WITH their terminators, so that joining them gives the text back exactly.
    $list = New-Object 'System.Collections.Generic.List[string]'
    $start = 0
    while ($start -lt $text.Length) {
        $nl = $text.IndexOf("`n", $start)
        if ($nl -lt 0) { $list.Add($text.Substring($start)); break }
        $list.Add($text.Substring($start, $nl - $start + 1))
        $start = $nl + 1
    }
    return ,$list
}

function Body([string]$line) { return $line.TrimEnd("`r", "`n") }
function Is-Blank([string]$line) { return (Body $line).Trim().Length -eq 0 }
function Is-Comment([string]$line) { return (Body $line).TrimStart().StartsWith('--') }
function Is-Go([string]$line) { return (Body $line) -match '^\s*GO\s*$' }

# `-- <KEY> <box><box>...` (KEY must not start with the box char). Returns KEY or $null.
$markerRe = New-Object Text.RegularExpressions.Regex ('^-- (?<key>[^' + $BOX + '\s][^' + $BOX + ']*?)\s*' + $BOX + $BOX)
function Get-MarkerKey([string]$line) {
    $m = $markerRe.Match((Body $line))
    if ($m.Success) { return $m.Groups['key'].Value.Trim() } else { return $null }
}
function Is-EndMarker([string]$line) { return (Body $line).Contains($END_WORDS) }
function Norm-Key([string]$key) {
    $k = [Text.RegularExpressions.Regex]::Replace($key, '\s+\d{4}-\d{2}-\d{2}$', '')
    return [Text.RegularExpressions.Regex]::Replace($k, '\s+', '')
}

# Second `<box><box>` segment of a begin marker when it is a short code (ECR-PRM-0404, preview).
function Get-SubLabel([string]$line) {
    $parts = (Body $line) -split ([Text.RegularExpressions.Regex]::Escape($BOX) + '+')
    if ($parts.Length -lt 3) { return $null }
    $seg = $parts[1].Trim()
    if ($seg -cmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$') { return $seg }
    return $null
}

$translit = @{}
$pairs = @(
    @(0x0410,'A'),@(0x0411,'B'),@(0x0412,'V'),@(0x0413,'H'),@(0x0490,'G'),@(0x0414,'D'),@(0x0415,'E'),@(0x0404,'YE'),
    @(0x0416,'ZH'),@(0x0417,'Z'),@(0x0418,'Y'),@(0x0406,'I'),@(0x0407,'YI'),@(0x0419,'Y'),@(0x041A,'K'),@(0x041B,'L'),
    @(0x041C,'M'),@(0x041D,'N'),@(0x041E,'O'),@(0x041F,'P'),@(0x0420,'R'),@(0x0421,'S'),@(0x0422,'T'),@(0x0423,'U'),
    @(0x0424,'F'),@(0x0425,'KH'),@(0x0426,'TS'),@(0x0427,'CH'),@(0x0428,'SH'),@(0x0429,'SHCH'),@(0x042C,''),@(0x042E,'YU'),
    @(0x042F,'YA'),@(0x042B,'Y'),@(0x042D,'E'),@(0x042A,''))
foreach ($p in $pairs) {
    $translit[[string][char]$p[0]] = $p[1]
    $lower = switch ($p[0]) { 0x0404 { 0x0454 } 0x0406 { 0x0456 } 0x0407 { 0x0457 } 0x0490 { 0x0491 } default { $p[0] + 0x20 } }
    $translit[[string][char]$lower] = $p[1].ToLowerInvariant()
}
function To-Ascii([string]$s) {
    $sb = New-Object Text.StringBuilder
    foreach ($ch in $s.ToCharArray()) {
        $c = [string]$ch
        if ([int]$ch -lt 128) { [void]$sb.Append($c) } elseif ($translit.ContainsKey($c)) { [void]$sb.Append($translit[$c]) }
    }
    return $sb.ToString()
}
function Norm-Area([string]$s) {
    $a = ([Text.RegularExpressions.Regex]::Replace((To-Ascii $s).ToUpperInvariant(), '[^A-Z0-9]', ''))
    if ($a.Length -eq 0) { $a = 'X' }
    return $a
}
function Norm-Slug([string]$s) {
    $t = To-Ascii $s
    $t = [Text.RegularExpressions.Regex]::Replace($t, '([a-z0-9])([A-Z])', '$1-$2')
    $t = $t.ToLowerInvariant()
    $t = [Text.RegularExpressions.Regex]::Replace($t, '[^a-z0-9]+', '-').Trim('-')
    if ($t.Length -eq 0) { $t = 'section' }
    return $t
}


# ---- scaffolding: the only text the split adds (exact; -Verify removes it again) ----
# ASCII here on purpose (PS 5.1 reads a BOM-less script as ANSI): `~~` -> two box chars,
# `{END}` -> the Ukrainian "end of section" words that every section end marker carries.
$NL = "`n"
$scaffoldPrelude = ((@(
    '',
    '-- SEED-SPLIT ~~ temp tables for changed/removed texts of seed/features files ~~',
    '-- A feature file that changes the text of an existing key adds (key, lang, old, new) to #UiChanged;',
    '-- one that removes a key adds (key, lang, last value) to #UiRemoved. 99-epilogue.sql applies both',
    '-- with the same condition as the shared lists above: only while the stored value is still the old one.',
    'DROP TABLE IF EXISTS #UiChanged;',
    'CREATE TABLE #UiChanged',
    '(',
    '    [Key]  nvarchar(200)  COLLATE DATABASE_DEFAULT NOT NULL,',
    '    Lang   nvarchar(8)    COLLATE DATABASE_DEFAULT NOT NULL,',
    '    OldVal nvarchar(1000) COLLATE DATABASE_DEFAULT NOT NULL,',
    '    NewVal nvarchar(1000) COLLATE DATABASE_DEFAULT NOT NULL',
    ');',
    'DROP TABLE IF EXISTS #UiRemoved;',
    'CREATE TABLE #UiRemoved',
    '(',
    '    [Key]  nvarchar(200)  COLLATE DATABASE_DEFAULT NOT NULL,',
    '    Lang   nvarchar(8)    COLLATE DATABASE_DEFAULT NOT NULL,',
    '    OldVal nvarchar(1000) COLLATE DATABASE_DEFAULT NOT NULL',
    ');',
    'GO',
    '-- SEED-SPLIT ~~ {END} ~~',
    '') -join $NL).Replace('~~', $BOX + $BOX)).Replace('{END}', $END_WORDS)
$scaffoldEpilogue = ((@(
    '-- SEED-SPLIT ~~ changed and removed texts from seed/features files ~~',
    '-- Same condition as the shared lists in 00-prelude.sql: a row is updated/deleted only while it still holds',
    '-- the old value (an administrator edit from /admin/ui-strings is kept). Revision is bumped only if a row',
    '-- changed. Both tables may be empty - then the batch changes nothing.',
    'DECLARE @featureTextUpdates int, @featureRemoved int;',
    'UPDATE t',
    '   SET Value = s.NewVal, ModifiedAt = SYSUTCDATETIME()',
    '  FROM sys_ecr.UiString AS t',
    '  JOIN #UiChanged AS s',
    '    ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang',
    ' WHERE t.Value = s.OldVal COLLATE Latin1_General_BIN2;',
    'SET @featureTextUpdates = @@ROWCOUNT;',
    'DELETE t',
    '  FROM sys_ecr.UiString AS t',
    '  JOIN #UiRemoved AS s',
    '    ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang',
    ' WHERE t.Value = s.OldVal COLLATE Latin1_General_BIN2;',
    'SET @featureRemoved = @@ROWCOUNT;',
    'IF @featureTextUpdates > 0 OR @featureRemoved > 0',
    'BEGIN',
    '    UPDATE sys_ecr.UiStringRevision',
    '    SET Revision = Revision + 1, ModifiedAt = SYSUTCDATETIME()',
    '    WHERE Id = 1;',
    'END',
    'DROP TABLE #UiChanged;',
    'DROP TABLE #UiRemoved;',
    'GO',
    '-- SEED-SPLIT ~~ {END} ~~',
    '',
    '') -join $NL).Replace('~~', $BOX + $BOX)).Replace('{END}', $END_WORDS)

# ---- plan ----
function Get-Plan([string]$text) {
    $lines = Split-Lines $text
    $n = $lines.Count

    $create = -1
    for ($i = 0; $i -lt $n; $i++) { if ((Body $lines[$i]) -match '^CREATE TABLE #I18N\b') { $create = $i; break } }
    if ($create -lt 0) { throw 'No `CREATE TABLE #I18N` - not the expected seed layout.' }
    $regionStart = -1
    for ($i = $create; $i -lt $n; $i++) { if (Is-Go $lines[$i]) { $regionStart = $i + 1; break } }

    $merges = @()
    for ($i = $regionStart; $i -lt $n; $i++) {
        if ((Body $lines[$i]).TrimEnd() -eq 'MERGE sys_ecr.UiString AS t') {
            for ($k = $i + 1; $k -lt [Math]::Min($n, $i + 8); $k++) { if ((Body $lines[$k]).Contains('FROM #I18N AS v')) { $merges += $i; break } }
        }
    }
    if ($merges.Count -ne 1) { throw "Expected exactly one ``MERGE sys_ecr.UiString AS t ... FROM #I18N``, found $($merges.Count)." }
    $merge = $merges[0]
    $lastGo = -1
    for ($i = $merge - 1; $i -ge $regionStart; $i--) { if (Is-Go $lines[$i]) { $lastGo = $i; break } }

    $sections = New-Object 'System.Collections.Generic.List[object]'
    $skipped = New-Object 'System.Collections.Generic.List[string]'
    $i = $regionStart
    while ($i -le $lastGo) {
        $key = Get-MarkerKey $lines[$i]
        if ($null -eq $key -or (Is-EndMarker $lines[$i])) { $i++; continue }
        $reason = $null
        # (1) starts at a batch boundary
        $b = $i - 1
        while ($b -ge $regionStart -and ((Is-Blank $lines[$b]) -or (Is-Comment $lines[$b]))) { $b-- }
        if ($b -ge $regionStart -and -not (Is-Go $lines[$b])) { $reason = 'does not start right after GO' }
        # (2) own end marker, nothing else in between
        $j = -1
        if (-not $reason) {
            for ($k = $i + 1; $k -lt $merge; $k++) {
                $k2 = Get-MarkerKey $lines[$k]
                if ($null -eq $k2) { continue }
                if (Is-EndMarker $lines[$k]) {
                    if ((Norm-Key $k2) -eq (Norm-Key $key)) { $j = $k } else { $reason = "end marker of another key ($k2) at line $($k + 1)" }
                } else { $reason = "another section starts at line $($k + 1) before the end marker" }
                break
            }
            if (-not $reason -and $j -lt 0) { $reason = 'no end marker' }
        }
        # (3) body = only `INSERT INTO #I18N ... OPTION (RECOMPILE);` + GO batches
        if (-not $reason) {
            $code = @()
            for ($k = $i + 1; $k -lt $j; $k++) { if (-not (Is-Blank $lines[$k]) -and -not (Is-Comment $lines[$k])) { $code += , (Body $lines[$k]).Trim() } }
            if ($code.Count -eq 0 -or $code[$code.Count - 1] -ne 'GO') { $reason = 'body does not end with GO' }
            else {
                $batch = @()
                foreach ($c in $code) {
                    if ($c -eq 'GO') {
                        if ($batch.Count -lt 4 -or $batch[0] -ne 'INSERT INTO #I18N ([Key], Lang, Val)' -or $batch[1] -ne 'SELECT v.[Key], v.Lang, v.Val' -or $batch[$batch.Count - 1] -ne 'OPTION (RECOMPILE);' -or $batch[$batch.Count - 2] -ne ') AS v ([Key], Lang, Val)') {
                            $reason = 'a batch is not exactly INSERT INTO #I18N ... OPTION (RECOMPILE);'; break
                        }
                        foreach ($c2 in $batch) { if ($c2.Contains('/*')) { $reason = 'block comment inside'; break } }
                        $batch = @()
                    } else { $batch += , $c }
                }
            }
        }
        if ($reason) { $skipped.Add("line $($i + 1) [$key]: $reason"); $i++; continue }
        $end = $j + 1
        while ($end -lt $merge -and (Is-Blank $lines[$end])) { $end++ }
        $area = $key; $slug = $key
        $colon = $key.IndexOf(':')
        if ($colon -gt 0) { $area = $key.Substring(0, $colon); $slug = $key.Substring($colon + 1).Trim() }
        $sections.Add([pscustomobject]@{ Start = $i; End = $end; Key = $key; Base = ((Norm-Area $area) + '-' + (Norm-Slug $slug)); Sub = (Get-SubLabel $lines[$i]); Name = $null })
        $i = $end
    }

    # names: deterministic, unique case-insensitively (Windows file system)
    $groups = $sections | Group-Object { $_.Base.ToLowerInvariant() }
    foreach ($g in $groups) {
        if ($g.Count -eq 1) { $g.Group[0].Name = $g.Group[0].Base; continue }
        $idx = 0
        foreach ($s in $g.Group) { $idx++; if ($s.Sub) { $s.Name = $s.Base + '-' + (Norm-Slug $s.Sub) } else { $s.Name = $s.Base + '-part' + $idx } }
    }
    $seen = @{}
    foreach ($s in $sections) {
        $nm = $s.Name; $c = 1
        while ($seen.ContainsKey($nm.ToLowerInvariant())) { $c++; $nm = $s.Name + '-' + $c }
        $s.Name = $nm; $seen[$nm.ToLowerInvariant()] = $true
        if ($s.Name -cnotmatch '^[A-Z0-9]+-[a-z0-9-]+$') { throw "Derived name '$($s.Name)' for [$($s.Key)] breaks ^[A-Z0-9]+-[a-z0-9-]+$." }
    }

    $epi = $lastGo + 1
    foreach ($s in $sections) { if ($s.End -gt $epi) { $epi = $s.End } }

    # segments in original order
    $segments = New-Object 'System.Collections.Generic.List[object]'
    $pos = 0
    foreach ($s in $sections) {
        if ($s.Start -gt $pos) { $segments.Add([pscustomobject]@{ Kind = 'prelude'; Start = $pos; End = $s.Start; Name = $null }) }
        $segments.Add([pscustomobject]@{ Kind = 'feature'; Start = $s.Start; End = $s.End; Name = $s.Name })
        $pos = $s.End
    }
    if ($epi -gt $pos) { $segments.Add([pscustomobject]@{ Kind = 'prelude'; Start = $pos; End = $epi; Name = $null }) }
    $segments.Add([pscustomobject]@{ Kind = 'epilogue'; Start = $epi; End = $n; Name = $null })

    foreach ($seg in $segments) {
        $sb = New-Object Text.StringBuilder
        for ($k = $seg.Start; $k -lt $seg.End; $k++) { [void]$sb.Append($lines[$k]) }
        $seg | Add-Member -NotePropertyName Text -NotePropertyValue $sb.ToString()
    }
    $prelude = (($segments | Where-Object Kind -eq 'prelude' | ForEach-Object Text) -join '') + $scaffoldPrelude
    $epilogue = $scaffoldEpilogue + (($segments | Where-Object Kind -eq 'epilogue' | ForEach-Object Text) -join '')
    $features = [ordered]@{}
    foreach ($seg in ($segments | Where-Object Kind -eq 'feature')) { $features[$seg.Name] = $seg.Text }

    return [pscustomobject]@{
        Lines = $lines; Segments = $segments; Sections = $sections; Skipped = $skipped
        Prelude = $prelude; Epilogue = $epilogue; Features = $features
        RegionStart = $regionStart; Merge = $merge; EpilogueStart = $epi
    }
}

function Get-RunOrder($prelude, $features, $epilogue) {
    $names = [string[]]@($features.Keys)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append($prelude)
    foreach ($nm in $names) { [void]$sb.Append($features[$nm]) }
    [void]$sb.Append($epilogue)
    return $sb.ToString()
}

function Sha([byte[]]$b) { $h = [Security.Cryptography.SHA256]::Create(); return ([BitConverter]::ToString($h.ComputeHash($b))).Replace('-', '').Substring(0, 16) }

function Same-Bytes([byte[]]$a, [byte[]]$b) {
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}

# ---- main ----
if (-not (Test-Path -LiteralPath $Source)) { throw "Source not found: $Source" }
$src = Read-Exact $Source
$plan = Get-Plan $src.Text

$preludeName = '00-prelude.sql'
$epilogueName = '99-epilogue.sql'

if (-not $Verify) {
    if (-not (Test-Path -LiteralPath $featDir)) { [void](New-Item -ItemType Directory -Path $featDir) }
    foreach ($old in [IO.Directory]::GetFiles($featDir, '*.sql')) { [IO.File]::Delete($old) }
    [IO.File]::WriteAllBytes([IO.Path]::Combine($OutDir, $preludeName), (Get-Bytes $plan.Prelude $src.Bom))
    [IO.File]::WriteAllBytes([IO.Path]::Combine($OutDir, $epilogueName), (Get-Bytes $plan.Epilogue $false))
    foreach ($nm in $plan.Features.Keys) {
        [IO.File]::WriteAllBytes([IO.Path]::Combine($featDir, $nm + '.sql'), (Get-Bytes $plan.Features[$nm] $false))
    }
}

# ---- verify (always, also right after a split) ----
$problems = New-Object 'System.Collections.Generic.List[string]'
$diskPrelude = Read-Exact ([IO.Path]::Combine($OutDir, $preludeName))
$diskEpilogue = Read-Exact ([IO.Path]::Combine($OutDir, $epilogueName))
if (-not (Same-Bytes $diskPrelude.Bytes (Get-Bytes $plan.Prelude $src.Bom))) { $problems.Add("$preludeName differs from the plan") }
if (-not (Same-Bytes $diskEpilogue.Bytes (Get-Bytes $plan.Epilogue $false))) { $problems.Add("$epilogueName differs from the plan") }
$diskFeatures = @{}
foreach ($f in [IO.Directory]::GetFiles($featDir, '*')) {
    $nm = [IO.Path]::GetFileNameWithoutExtension($f)
    if (-not $f.EndsWith('.sql')) { $problems.Add("unexpected file in features/: $([IO.Path]::GetFileName($f))"); continue }
    if (-not $plan.Features.Contains($nm)) { $problems.Add("feature on disk not in plan: $nm.sql"); continue }
    $d = Read-Exact $f
    if ($d.Bom) { $problems.Add("feature $nm.sql has a BOM") }
    if (-not (Same-Bytes $d.Bytes (Get-Bytes $plan.Features[$nm] $false))) { $problems.Add("feature $nm.sql differs from the plan") }
    $diskFeatures[$nm] = $d.Text
}
foreach ($nm in $plan.Features.Keys) { if (-not $diskFeatures.ContainsKey($nm)) { $problems.Add("planned feature missing on disk: $nm.sql") } }
foreach ($f in [IO.Directory]::GetFiles($OutDir, '*.sql')) {
    $leaf = [IO.Path]::GetFileName($f)
    if ($leaf -ne $preludeName -and $leaf -ne $epilogueName) { $problems.Add("unexpected file in seed/: $leaf") }
}

# (b) reconstruction FROM DISK: prelude pieces cut by the planned lengths, features put back at their original positions.
$reconstructed = $null
if ($problems.Count -eq 0) {
    $p = $diskPrelude.Text
    if (-not $p.EndsWith($scaffoldPrelude)) { $problems.Add('prelude does not end with the scaffolding') }
    $e = $diskEpilogue.Text
    if (-not $e.StartsWith($scaffoldEpilogue)) { $problems.Add('epilogue does not start with the scaffolding') }
    if ($problems.Count -eq 0) {
        $p = $p.Substring(0, $p.Length - $scaffoldPrelude.Length)
        $e = $e.Substring($scaffoldEpilogue.Length)
        $sb = New-Object Text.StringBuilder
        $cursor = 0
        foreach ($seg in $plan.Segments) {
            switch ($seg.Kind) {
                'prelude' { [void]$sb.Append($p.Substring($cursor, $seg.Text.Length)); $cursor += $seg.Text.Length }
                'feature' { [void]$sb.Append($diskFeatures[$seg.Name]) }
                'epilogue' { [void]$sb.Append($e) }
            }
        }
        if ($cursor -ne $p.Length) { $problems.Add('prelude has text the plan does not account for') }
        $reconstructed = Get-Bytes $sb.ToString() $src.Bom
        if (-not (Same-Bytes $reconstructed $src.Bytes)) { $problems.Add('reconstruction is NOT byte-identical to the original') }
    }
}

# (d) run order = same multiset of lines as the original + scaffolding.
$runOrder = Get-RunOrder $diskPrelude.Text $diskFeatures $diskEpilogue.Text
$a = [string[]]@((Split-Lines $runOrder) | ForEach-Object { Body $_ })
$bList = New-Object 'System.Collections.Generic.List[string]'
foreach ($l in $plan.Lines) { $bList.Add((Body $l)) }
foreach ($l in (Split-Lines ($scaffoldPrelude + $scaffoldEpilogue))) { $bList.Add((Body $l)) }
$b = $bList.ToArray()
[Array]::Sort($a, [StringComparer]::Ordinal); [Array]::Sort($b, [StringComparer]::Ordinal)
if ($a.Length -ne $b.Length) { $problems.Add("run-order line count $($a.Length) != original+scaffolding $($b.Length)") }
else { for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -cne $b[$i]) { $problems.Add('run-order lines differ from original+scaffolding'); break } } }

if (-not $Quiet) {
    $movedLines = 0; foreach ($s in $plan.Sections) { $movedLines += ($s.End - $s.Start) }
    Write-Host ("source      {0}  {1} bytes, {2} lines, BOM={3}, sha256={4}" -f $Source, $src.Bytes.Length, $plan.Lines.Count, $src.Bom, (Sha $src.Bytes))
    Write-Host ("#I18N region lines {0}..{1}; epilogue from line {2}" -f ($plan.RegionStart + 1), ($plan.Merge + 1), ($plan.EpilogueStart + 1))
    Write-Host ("features    {0} files, {1} lines moved; prelude {2} lines; epilogue {3} lines" -f $plan.Features.Count, $movedLines, (Split-Lines $plan.Prelude).Count, (Split-Lines $plan.Epilogue).Count)
    Write-Host ("named markers in the #I18N region left in the prelude: {0}" -f $plan.Skipped.Count)
    foreach ($s in $plan.Skipped) { Write-Host "  stays: $s" }
    if ($reconstructed) { Write-Host ("reconstruct sha256={0} (original {1})" -f (Sha $reconstructed), (Sha $src.Bytes)) }
    Write-Host ("run-order   sha256={0}, {1} lines" -f (Sha (Get-Bytes $runOrder $false)), $a.Length)
}
if ($problems.Count -gt 0) {
    foreach ($x in $problems) { Write-Host "FAIL: $x" }
    exit 1
}
Write-Host 'OK: byte-identical reconstruction, run order = same lines + scaffolding.'
if (-not $Verify -and -not $KeepSource) {
    [IO.File]::Delete($Source)
    Write-Host "removed $Source"
}
exit 0
