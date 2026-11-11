<#
    Pester (3/5 compatible syntax, no `Should`): tools/land-test-doc/LandTestDocHeader.Lib.ps1 - contract header of the
    Land test document (land-test-doc.header.csv): validation (placeholders = missing), diff against GET .../header,
    PATCH body, idempotency (a converged header gives no change). No network.
#>

Describe 'Land test document: contract header' {
    It 'data file: every header field of the manifest once; placeholders are MISSING; -PermitNumber fills <PERMIT>' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land-test-doc/LandTestDocHeader.Lib.ps1')
        $items = @(Read-LandTestHeader -Path (Join-Path $repo 'tools/land-test-doc/land-test-doc.header.csv'))
        $manifest = Get-Content -LiteralPath (Join-Path $repo 'docs/delivery/reference-data/land-contract/header-fields.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $want = @($manifest.fields | Where-Object { $_.kind -ne 'Service' } | ForEach-Object { [string]$_.key } | Sort-Object)
        $have = @($items | ForEach-Object { [string]$_.code } | Sort-Object)
        if (($want -join ',') -ne ($have -join ',')) { throw "fields: $($have -join ',') vs manifest $($want -join ',')" }
        foreach ($m in @($manifest.fields | Where-Object { $_.kind -ne 'Service' })) {
            $i = @($items | Where-Object { $_.code -eq $m.key })[0]
            $k = if ($m.kind -eq 'Lookup') { 'lookup' } else { 'text' }
            if ($i.kind -ne $k) { throw "$($m.key): kind $($i.kind), manifest $($m.kind)" }
            if ([string]::IsNullOrWhiteSpace($i.source)) { throw "$($m.key): no source" }
        }
        $p = Test-LandTestHeader -Items $items
        $p = @($p)   # the function returns the list as one object (unary comma)
        if (@($p | Where-Object { $_ -like 'HEADER MISSING VALUE Permit*<PERMIT>*' }).Count -ne 1) { throw "Permit placeholder not reported: $($p -join ' | ')" }
        $filled = Set-LandTestHeaderPermit -Items $items -PermitNumber 'KZ01TEST0001'
        if (@($filled | Where-Object { $_.code -eq 'Permit' })[0].value -ne 'KZ01TEST0001') { throw 'Permit not replaced' }
        if (@($items | Where-Object { $_.code -eq 'Permit' })[0].value -ne '<PERMIT>') { throw 'input changed' }
        $p2 = Test-LandTestHeader -Items $filled
        $p2 = @($p2)
        if (@($p2 | Where-Object { $_ -like '*Permit*' }).Count -ne 0) { throw "Permit still missing: $($p2 -join ' | ')" }
        if (@($p2 | Where-Object { $_ -notlike 'HEADER MISSING VALUE * placeholder <PICK:*' }).Count -ne 0) { throw "other problems: $($p2 -join ' | ')" }
    }

    It 'validation: empty value, empty source, bad kind, duplicate, bad date and number' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land-test-doc/LandTestDocHeader.Lib.ps1')
        $items = @(
            [pscustomobject]@{ code = 'A'; kind = 'text'; value = ''; source = 's' }
            [pscustomobject]@{ code = 'B'; kind = 'text'; value = 'x'; source = '' }
            [pscustomobject]@{ code = 'C'; kind = 'bool'; value = 'x'; source = 's' }
            [pscustomobject]@{ code = 'B'; kind = 'text'; value = 'y'; source = 's' }
            [pscustomobject]@{ code = 'D'; kind = 'date'; value = '30.09.2026'; source = 's' }
            [pscustomobject]@{ code = 'E'; kind = 'number'; value = '1,5'; source = 's' }
            [pscustomobject]@{ code = 'F'; kind = 'date'; value = '2026-09-30'; source = 's' }
        )
        $p = Test-LandTestHeader -Items $items
        $p = @($p)
        foreach ($w in 'HEADER MISSING VALUE A*', 'HEADER MISSING SOURCE B*', 'HEADER BAD KIND C*', 'HEADER DUPLICATE B', 'HEADER NOT A DATE D*', 'HEADER NOT A NUMBER E*') {
            if (@($p | Where-Object { $_ -like $w }).Count -ne 1) { throw "expected '$w' in: $($p -join ' | ')" }
        }
        if (@($p | Where-Object { $_ -like '* F*' }).Count -ne 0) { throw 'valid date reported' }
        if (@($p).Count -ne 6) { throw "problems: $($p -join ' | ')" }
    }

    It 'plan: only changed fields, lookup by registry, PATCH body with baseVersion; converged header -> 0 changes' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land-test-doc/LandTestDocHeader.Lib.ps1')
        $items = @(
            [pscustomobject]@{ code = 'Region'; kind = 'lookup'; value = 'REG_002'; source = 's' }
            [pscustomobject]@{ code = 'FilledBy'; kind = 'text'; value = 'test'; source = 's' }
            [pscustomobject]@{ code = 'ProcessedOn'; kind = 'text'; value = '2026-09-30'; source = 's' }
            [pscustomobject]@{ code = 'Area'; kind = 'lookup'; value = '<PICK:LAND_AREA>'; source = 's' }
        )
        $header = '{"fields":[{"code":"Area","dataType":"Lookup","value":null,"lookupRegistryDefId":40},{"code":"Region","dataType":"Lookup","value":null,"lookupRegistryDefId":41},{"code":"FilledBy","dataType":"String","value":"test","lookupRegistryDefId":null},{"code":"ProcessedOn","dataType":"String","value":null,"lookupRegistryDefId":null}],"version":"ABC"}' | ConvertFrom-Json
        $script:Asked = New-Object System.Collections.ArrayList
        $resolve = { param($RegistryDefId, $Value) [void]$script:Asked.Add("$RegistryDefId/$Value"); if ($Value -eq 'REG_002') { return 702 } throw "unexpected $Value" }
        $plan = Get-LandTestHeaderPlan -Items $items -Header $header -ResolveEntry $resolve
        $codes = @($plan.Changes | ForEach-Object { $_.Code }) -join ','
        if ($codes -ne 'Region,ProcessedOn') { throw "changes: $codes" }
        if ($plan.Unchanged -ne 1) { throw "unchanged $($plan.Unchanged)" }
        if (($script:Asked -join ';') -ne '41/REG_002') { throw "resolver: $($script:Asked -join ';')" }
        $body = New-LandTestHeaderPatchBody -Changes $plan.Changes -BaseVersion 'ABC'
        if ($body.baseVersion -ne 'ABC' -or @($body.fields).Count -ne 2) { throw 'body shape' }
        $json = $body | ConvertTo-Json -Depth 5 -Compress
        if ($json -notlike '*"code":"Region"*' -or $json -notlike '*"value":702*' -or $json -notlike '*"isEmpty":false*' -or $json -notlike '*"value":"2026-09-30"*') { throw "json: $json" }

        # second run: the server returns what was written (lookup id as number) -> nothing to write
        $after = '{"fields":[{"code":"Area","dataType":"Lookup","value":null,"lookupRegistryDefId":40},{"code":"Region","dataType":"Lookup","value":702,"lookupRegistryDefId":41},{"code":"FilledBy","dataType":"String","value":"test","lookupRegistryDefId":null},{"code":"ProcessedOn","dataType":"String","value":"2026-09-30","lookupRegistryDefId":null}],"version":"DEF"}' | ConvertFrom-Json
        $again = Get-LandTestHeaderPlan -Items $items -Header $after -ResolveEntry $resolve
        if (@($again.Changes).Count -ne 0 -or $again.Unchanged -ne 3) { throw "not idempotent: $(@($again.Changes).Count) changes" }
    }

    It 'plan refuses: field absent from the template version, kind vs dataType mismatch; date compares by day' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land-test-doc/LandTestDocHeader.Lib.ps1')
        $header = '{"fields":[{"code":"ProcessedOn","dataType":"Date","value":"2026-09-30T00:00:00","lookupRegistryDefId":null}],"version":"V"}' | ConvertFrom-Json
        $resolve = { param($RegistryDefId, $Value) throw 'no lookup expected' }
        $msg = $null
        try { $null = Get-LandTestHeaderPlan -Items @([pscustomobject]@{ code = 'Nope'; kind = 'text'; value = 'x'; source = 's' }) -Header $header -ResolveEntry $resolve } catch { $msg = $_.Exception.Message }
        if ($msg -notlike "*'Nope' is not in the template version*") { throw "absent field: $msg" }
        $msg = $null
        try { $null = Get-LandTestHeaderPlan -Items @([pscustomobject]@{ code = 'ProcessedOn'; kind = 'text'; value = 'x'; source = 's' }) -Header $header -ResolveEntry $resolve } catch { $msg = $_.Exception.Message }
        if ($msg -notlike "*is Date in the template*kind 'text'*") { throw "kind mismatch: $msg" }
        $same = Get-LandTestHeaderPlan -Items @([pscustomobject]@{ code = 'ProcessedOn'; kind = 'date'; value = '2026-09-30'; source = 's' }) -Header $header -ResolveEntry $resolve
        if (@($same.Changes).Count -ne 0) { throw 'same day must be converged' }
    }

    It 'entry match: code first, else display (permit number); ambiguous or absent -> error' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land-test-doc/LandTestDocHeader.Lib.ps1')
        $entries = @(
            [pscustomobject]@{ id = 11; code = 'E000123'; display = 'KZ01TEST0001' }
            [pscustomobject]@{ id = 12; code = 'KZ01TEST0001'; display = 'other' }
            [pscustomobject]@{ id = 13; code = 'E000124'; display = ' KZ02TEST0002 ' }
            [pscustomobject]@{ id = 14; code = 'E000125'; display = 'DUP' }
            [pscustomobject]@{ id = 15; code = 'E000126'; display = 'DUP' }
        )
        if ((Select-LandTestHeaderEntry -Entries $entries -Value 'KZ01TEST0001' -RegistryCode 'R') -ne 12) { throw 'code must win' }
        if ((Select-LandTestHeaderEntry -Entries $entries -Value 'KZ02TEST0002' -RegistryCode 'R') -ne 13) { throw 'display match' }
        $msg = $null
        try { $null = Select-LandTestHeaderEntry -Entries $entries -Value 'DUP' -RegistryCode 'R' } catch { $msg = $_.Exception.Message }
        if ($msg -notlike '*2 usable entries*') { throw "ambiguous: $msg" }
        $msg = $null
        try { $null = Select-LandTestHeaderEntry -Entries $entries -Value 'NONE' -RegistryCode 'R' } catch { $msg = $_.Exception.Message }
        if ($msg -notlike "*no entry 'NONE' usable*") { throw "absent: $msg" }
    }

    It 'Fill-LandTestDocument.ps1: -HeaderFile, -SkipHeader switch, -PermitNumber; header PATCH only under -Execute' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $errs = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools/land-test-doc/Fill-LandTestDocument.ps1'), [ref]$null, [ref]$errs)
        if (@($errs).Count -ne 0) { throw "parse errors: $(@($errs).Count)" }
        $names = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
        foreach ($n in 'HeaderFile', 'SkipHeader', 'PermitNumber') { if ($names -notcontains $n) { throw "no -$n" } }
        $sw = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'SkipHeader' }
        if ($sw.StaticType -ne [switch]) { throw '-SkipHeader must be a switch' }
        $text = $ast.Extent.Text
        $i = $text.IndexOf('PATCH "/api/v1/documents/$docId/header"')
        if ($i -lt 0) { throw 'no header PATCH' }
        if ($text.LastIndexOf('if ($exec -and @($hplan.Changes).Count -gt 0)', $i) -lt 0) { throw 'header PATCH not guarded by -Execute' }
    }
}
