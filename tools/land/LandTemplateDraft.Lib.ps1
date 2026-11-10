# tools/land/LandTemplateDraft.Lib.ps1
# З якої версії Stage-Template (Build-LandTemplate.ps1) будує шаблон. Рішення координатора 10.11 (репетиція §4' на
# EcrLandMB): ЗАВЖДИ свіжий клон останньої ОПУБЛІКОВАНОЇ версії, наявна чернетка не чіпається (у ній були чужа група G1
# і застарілий MonthNumber T3_00.Land_NAE). Стара поведінка — лише явним -ReuseDraft.

function Get-LandNextVersionNumber {
    <#
    .SYNOPSIS
        Наступний номер за правилом Apply-ContractHeader.ps1 (третя частина +1: 1.0.14.0 -> 1.0.15.0); зайнятий номер
        (наприклад, у старої чернетки) пропускається далі.
    #>
    param([Parameter(Mandatory)][string]$FromVersion, [string[]]$Taken = @())
    $parts = $FromVersion.Split('.')
    if ($parts.Count -lt 3) { throw "Не вдалося обчислити номер нової версії з «$FromVersion»." }
    do {
        $parts[2] = [string]([int]$parts[2] + 1)
        $candidate = $parts -join '.'
    } while ($Taken -contains $candidate)
    return $candidate
}

function Get-LandTemplateDraftPlan {
    <#
    .SYNOPSIS
        Чисте рішення за переліком версій шаблону (id, version, status):
        Clone — свіжий клон останньої опублікованої (SourceId, NewVersion, SkippedDrafts);
        UseDraft — наявна чернетка (лише -ReuseDraft); PublishedNoOp — опублікована без чернетки (лише -ReuseDraft:
        структуру не чіпати, як раніше); CreateEmpty — опублікованої немає (порожня версія з номером моделі).
    #>
    param([object[]]$Versions, [switch]$ReuseDraft)
    $all = @($Versions | Where-Object { $null -ne $_ })
    $pub = $all | Where-Object { $_.status -eq 'Published' } | Sort-Object { [int]$_.id } -Descending | Select-Object -First 1
    $drafts = @($all | Where-Object { $_.status -eq 'Draft' } | Sort-Object { [int]$_.id } -Descending)
    if ($ReuseDraft) {
        if ($drafts.Count -gt 0) { return [pscustomobject]@{ Action = 'UseDraft'; DraftId = [int]$drafts[0].id; DraftVersion = [string]$drafts[0].version } }
        if ($null -ne $pub) { return [pscustomobject]@{ Action = 'PublishedNoOp'; PublishedId = [int]$pub.id; PublishedVersion = [string]$pub.version } }
        return [pscustomobject]@{ Action = 'CreateEmpty' }
    }
    if ($null -eq $pub) {
        if ($drafts.Count -gt 0) {
            throw ("Шаблон не має опублікованої версії, а чернетка $($drafts[0].version) (id $($drafts[0].id)) уже є: свіжого клону " +
                'зробити нема з чого. Вирішіть долю чернетки вручну або запустіть із -ReuseDraft.')
        }
        return [pscustomobject]@{ Action = 'CreateEmpty' }
    }
    $usedNumbers = @($all | ForEach-Object { [string]$_.version })
    return [pscustomobject]@{
        Action = 'Clone'; SourceId = [int]$pub.id; SourceVersion = [string]$pub.version
        NewVersion = (Get-LandNextVersionNumber -FromVersion ([string]$pub.version) -Taken $usedNumbers)
        SkippedDrafts = @($drafts | ForEach-Object { "$($_.version) (id $($_.id))" })
    }
}

function New-LandTemplateCloneDraft {
    <#
    .SYNOPSIS
        Виконує план Clone: POST /api/v1/template-versions/{SourceId}/clone { newVersion }. Стару чернетку не читає й не
        пише. Повертає id нової чернетки; відмову сервера кидає дослівно (код і текст), нічого не видаляючи.
    #>
    param([Parameter(Mandatory)]$Plan)
    $cl = Invoke-Ecr -Method POST -Path "/api/v1/template-versions/$($Plan.SourceId)/clone" -Body @{ newVersion = $Plan.NewVersion }
    if ($cl.Status -notin 200, 201) {
        throw ("Клон $($Plan.SourceVersion) (id $($Plan.SourceId)) -> $($Plan.NewVersion) відхилено: HTTP $($cl.Status) " +
            "$(Get-EcrErrorCode $cl) $($cl.Text). Стару чернетку не змінено й не видалено.")
    }
    return [int]$cl.Json.versionId
}
