# b14 document-move T2 (D-577, review H6): which HSE400 document of a year is the working one.
# Land reads the HSE400 composition of a year from the document with the LARGEST id (B80, any project except
# HSE400_GOLD). A second working document of the same year would silently become Land's source - so the builder
# never creates one: after the 26.11 move the working document lives in LANDUI2026 (monthly, together with Land).

function Resolve-Hse400WorkDocument {
    <#
      Pure decision (unit-tested in tools/tests/Hse400-WorkDocument.Tests.ps1).
      -Documents: HSE400 documents known to the stand, each @{ Id; ProjectCode; Year } (Year = project year).
      Returns the id of the document in -ProjectCode; $null when none exists anywhere for the year (the caller may create
      one); throws when the year already has a non-GOLD HSE400 document in ANOTHER project, or several in -ProjectCode.
    #>
    param(
        [Parameter(Mandatory)][string]$ProjectCode,
        [Parameter(Mandatory)][int]$Year,
        [AllowEmptyCollection()][object[]]$Documents = @(),
        [string]$GoldCode = 'HSE400_GOLD'
    )
    $sameYear = @($Documents | Where-Object { [int]$_.Year -eq $Year -and [string]$_.ProjectCode -ne $GoldCode })
    $own = @($sameYear | Where-Object { [string]$_.ProjectCode -eq $ProjectCode })
    if ($own.Count -gt 1) {
        throw ("У проєкті {0} уже {1} документи HSE400 за {2} ({3}): робочий має бути один." -f $ProjectCode, $own.Count, $Year, (($own | ForEach-Object { $_.Id }) -join ', '))
    }
    if ($own.Count -eq 1) { return [long]$own[0].Id }
    if ($sameYear.Count -gt 0) {
        $other = $sameYear[0]
        throw ("Робочий документ HSE400 за {0} уже є: id {1} у проєкті {2}. Другий документ того самого року став би джерелом Land " +
            "(найбільший id, B80). Вкажіть -ProjectCode {2} або перенесіть документ (tools/document-move).") -f $Year, $other.Id, $other.ProjectCode
    }
    return $null
}

function Test-Hse400MayCreateProject {
    <# Новий річний проєкт будівник створює лише зі старим кодом HSE400_<рік> (scratch); LANDUI2026 та інші - лише наявні. #>
    param([Parameter(Mandatory)][string]$ProjectCode, [Parameter(Mandatory)][int]$Year)
    return $ProjectCode -ceq ('HSE400_{0}' -f $Year)
}
