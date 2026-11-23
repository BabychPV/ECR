using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Обгортка запуску pwsh (<see cref="DeployScriptHarness.FindPowerShell"/>) дає кожному запуску ВЛАСНИЙ кеш PowerShell.
/// </summary>
/// <remarks>
/// ⛔ Причина 139 (✎ 2026-11-23): спільний <c>~/.cache/powershell/StartupProfileData-NonInteractive</c>, який паралельні
/// pwsh переписують на виході; битий файл валить SIGSEGV кожен наступний pwsh у job. Тест тримає саме ізоляцію:
/// два запуски - два різні каталоги кешу в тимчасовій теці, жоден не дорівнює спільному.
/// </remarks>
public sealed class PwshLauncherCacheIsolationTests
{
    private const string Body = """
        $cache = [Environment]::GetEnvironmentVariable('XDG_CACHE_HOME')
        "cache=$cache"
        "exists=$([System.IO.Directory]::Exists([string]$cache))"
        """;

    [Fact]
    public void Кожен_запуск_pwsh_має_власний_кеш_PowerShell_у_тимчасовому_каталозі()
    {
        var first = DeployScriptHarness.Run([], Body);
        var second = DeployScriptHarness.Run([], Body);

        if (!OperatingSystem.IsLinux())
        {
            // Windows-шлях не йде через обгортку (pwsh.exe/powershell.exe напряму): XDG там не діє.
            Assert.EndsWith(".exe", DeployScriptHarness.FindPowerShell(), StringComparison.OrdinalIgnoreCase);
            return;
        }

        var a = DeployScriptHarness.Value(first, "cache");
        var b = DeployScriptHarness.Value(second, "cache");
        Assert.False(string.IsNullOrEmpty(a), "обгортка не задала XDG_CACHE_HOME: pwsh пише спільний ~/.cache/powershell");
        Assert.NotEqual(a, b);
        Assert.Equal("True", DeployScriptHarness.Value(first, "exists"));

        var shared = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "/", ".cache");
        Assert.NotEqual(Path.TrimEndingDirectorySeparator(shared), Path.TrimEndingDirectorySeparator(a));
        Assert.StartsWith(Path.GetTempPath().TrimEnd('/'), a, StringComparison.Ordinal);
    }
}
