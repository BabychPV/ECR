using System.Diagnostics;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Виконує СПРАВЖНІ функції <c>tools/deploy-ecr.ps1</c>: вирізає їх парсером PowerShell (AST)
/// і запускає в <c>pwsh</c> (Linux-раннер CI) або <c>powershell.exe</c> (Windows без pwsh).
/// </summary>
/// <remarks>
/// ⛔ Предмет — функції скрипта, не їх копія (той самий підхід, що й у
/// <see cref="DeployWorkerModeTests"/>). Реєстру, msiexec, SQL і сховища сертифікатів тут немає.
/// <para>
/// ⚠ Тіло сценарію й весь вивід — лише ASCII: Windows PowerShell 5.1 читає файл без BOM у кодовій
/// сторінці ANSI, а вивід консолі — теж; кирилиця дала б хибний збій, не пов'язаний із логікою.
/// Рядки з кирилицею у функціях скрипта перевіряються за ASCII-ознаками (коди, лічильники,
/// збіг за латинським фрагментом), а не дослівно.
/// </para>
/// </remarks>
internal static class DeployScriptHarness
{
    private const string Prologue = """
        param([string] $Deploy)
        $ErrorActionPreference = 'Stop'
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Deploy, [ref] $tokens, [ref] $errors)
        if ($errors.Count) { throw "parse: $($errors[0].Message)" }
        """;

    // Без обгортки-функції: крапкове підключення всередині функції лишило б визначення в її власній області.
    private const string ImportTemplate = """
        $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq '__NAME__' }, $true)
        if (-not $fn) { throw "no function __NAME__" }
        . ([scriptblock]::Create($fn.Extent.Text))
        """;

    /// <summary>Виконує <paramref name="body"/> після імпорту <paramref name="functions"/>; повертає рядки <c>ключ=значення</c>.</summary>
    /// <param name="functions">Імена функцій зі скрипта розгортання.</param>
    /// <param name="body">ASCII-сценарій, що друкує рядки <c>ключ=значення</c>.</param>
    /// <param name="script">Скрипт у <c>tools/</c>, з якого вирізаються функції.</param>
    public static Dictionary<string, string> Run(string[] functions, string body, string script = "deploy-ecr.ps1")
    {
        var dir = Path.Combine(Path.GetTempPath(), "ecr-deploy-harness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var imports = string.Join(Environment.NewLine, functions.Select(f => ImportTemplate.Replace("__NAME__", f, StringComparison.Ordinal)));
            var harness = Path.Combine(dir, "harness.ps1");
            File.WriteAllText(harness, Prologue + Environment.NewLine + imports + Environment.NewLine + body);

            var start = new ProcessStartInfo(FindPowerShell())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness,
                         "-Deploy", Path.Combine(SourceTree.Root, "tools", script) })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell не завершився за 2 хв");
            }

            Assert.True(process.ExitCode == 0, $"PowerShell: код {process.ExitCode}\n{stderr.Result}\n{stdout.Result}");

            return stdout.Result
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Contains('=', StringComparison.Ordinal))
                .Select(l => l.Split('=', 2))
                .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Значення ключа з виводу; падає з усім виводом, якщо ключа немає.</summary>
    /// <param name="results">Результат <see cref="Run"/>.</param>
    /// <param name="key">Ключ.</param>
    public static string Value(IReadOnlyDictionary<string, string> results, string key)
    {
        Assert.True(results.TryGetValue(key, out var value), $"ключа {key} немає у виводі:\n{string.Join('\n', results)}");
        return value!;
    }

    /// <summary><c>pwsh</c> з PATH; на Windows без нього — вбудований <c>powershell.exe</c>.</summary>
    internal static string FindPowerShell()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "pwsh.exe", "powershell.exe" } : ["pwsh"];
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var name in names)
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // ⛔ Не пропуск: без PowerShell сторож мовчав би саме там, де його чекають.
        throw new InvalidOperationException("Немає pwsh у PATH (на Windows — і powershell.exe): тест не може виконати deploy-ecr.ps1.");
    }
}
