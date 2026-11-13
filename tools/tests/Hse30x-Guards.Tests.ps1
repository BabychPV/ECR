# tools/tests/Hse30x-Guards.Tests.ps1
# FLERT F4b: сторожі моделі шаблону HSE30X (tools/hse30x/Test-Hse30xGuards.ps1) — у CI тим самим кроком Pester, що й
# решта tools/tests. Сторожі живуть окремим скриптом (як Test-Hse401Guards.ps1) і ганяються окремим процесом тієї
# самої оболонки: код виходу 0 — усе зелене. Шляхи - з прямою рискою (Linux).
Describe 'HSE30X model guards' {
    BeforeAll {
        $root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $script:guards = Join-Path $root 'tools/hse30x/Test-Hse30xGuards.ps1'
        $script:shell = (Get-Process -Id $PID).Path
    }
    It 'усі сторожі зелені (код виходу 0, жодного FAIL)' {
        $psArgs = @('-NoProfile')
        if ($env:OS -eq 'Windows_NT') { $psArgs += @('-ExecutionPolicy', 'Bypass') }
        $out = & $script:shell @psArgs -File $script:guards 2>&1 | Out-String
        $code = $LASTEXITCODE
        if ($code -ne 0 -or $out -match '(?m)^FAIL') { throw "Test-Hse30xGuards.ps1: exit $code`n$out" }
        if ($out -notmatch '(?m)^PASS') { throw "Test-Hse30xGuards.ps1 не виконав жодної перевірки:`n$out" }
    }
}
