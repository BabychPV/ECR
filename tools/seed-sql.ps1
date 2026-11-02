<#
.SYNOPSIS
    Dot-source helper: the seed as ONE script, in the order SeedRunner executes it
    (src/Ecr.Infrastructure/Persistence/SeedScript.cs):
    seed/00-prelude.sql -> seed/features/*.sql (ordinal by name) -> seed/99-epilogue.sql.

.DESCRIPTION
    The parts share temp tables (#I18N, #UiChanged, #UiRemoved) created by the prelude and
    applied by the epilogue, so they must run in ONE session: sqlcmd -i per part would lose
    them. Tools that run the seed through sqlcmd write the composed file with Write-SeedSql
    and run that single file. Works on bytes (no Get-Content/Set-Content: they corrupt
    Cyrillic and add a BOM in PS 5.1); a BOM of a part is dropped, a missing final LF added.

.EXAMPLE
    . (Join-Path $PSScriptRoot 'seed-sql.ps1')
    Write-SeedSql -SqlDir $sql -Path (Join-Path $artifacts 'seed.sql')
#>

function Get-SeedSqlParts {
    param([Parameter(Mandatory)] [string] $SqlDir)

    $seedDir = [IO.Path]::Combine($SqlDir, 'seed')
    $prelude = [IO.Path]::Combine($seedDir, '00-prelude.sql')
    $epilogue = [IO.Path]::Combine($seedDir, '99-epilogue.sql')
    $featDir = [IO.Path]::Combine($seedDir, 'features')
    foreach ($required in @($prelude, $epilogue)) {
        if (-not [IO.File]::Exists($required)) { throw "seed part missing: $required" }
    }

    $unexpected = @([IO.Directory]::GetFiles($seedDir, '*', [IO.SearchOption]::AllDirectories) | Where-Object {
        $_ -ne $prelude -and $_ -ne $epilogue -and -not ([IO.Path]::GetDirectoryName($_) -eq $featDir -and $_.EndsWith('.sql'))
    })
    if ($unexpected.Count -gt 0) { throw "unexpected seed parts: $($unexpected -join ', ')" }

    $features = [string[]]@([IO.Directory]::GetFiles($featDir, '*.sql'))
    $names = [string[]]@($features | ForEach-Object { [IO.Path]::GetFileName($_) })
    [Array]::Sort($names, $features, [StringComparer]::Ordinal)

    return @($prelude) + $features + @($epilogue)
}

function Write-SeedSql {
    param(
        [Parameter(Mandatory)] [string] $SqlDir,
        [Parameter(Mandatory)] [string] $Path
    )

    $stream = New-Object IO.MemoryStream
    foreach ($part in (Get-SeedSqlParts -SqlDir $SqlDir)) {
        $bytes = [IO.File]::ReadAllBytes($part)
        $offset = 0
        if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $offset = 3 }
        $stream.Write($bytes, $offset, $bytes.Length - $offset)
        if ($bytes.Length -gt $offset -and $bytes[$bytes.Length - 1] -ne 10) { $stream.WriteByte(10) }
    }
    $dir = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))
    if (-not [IO.Directory]::Exists($dir)) { [void][IO.Directory]::CreateDirectory($dir) }
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Path), $stream.ToArray())
}
