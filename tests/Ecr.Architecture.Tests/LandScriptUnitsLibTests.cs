using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// <c>tools/land/LandScriptUnits.Lib.ps1</c> (D-556, коло 4): одиниці аргументів і констант у тексті скрипта. СИНТЕТИЧНІ тексти
/// (вигадані методологія й колонки), без мережі й БД.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): у <c>Set-LandScriptUnits</c> прибрати заміну <c>row.X</c> на <c>u_X</c> — червоніє
/// <c>Аргумент_стає_змінною_з_одиницею_і_замінюється_у_виразах</c>; послабити межу слова (<c>(?![A-Za-z0-9_])</c> прибрати) —
/// червоніє <c>Аргумент_із_спільним_початком_імені_не_чіпається</c>; прибрати перевірку «не знайдено» —
/// червоніє <c>Відсутній_аргумент_чи_константа_дає_помилку</c>; прибрати вставку оголошень — червоніє перший тест;
/// у гілці констант замінити ` : ` на ` = ` — червоніє <c>Константа_міняє_одиницю_а_число_лишається</c>.
/// </remarks>
public sealed class LandScriptUnitsLibTests
{
    private static string Lib => Path.Combine(SourceTree.Root, "tools", "land", "LandScriptUnits.Lib.ps1");

    private static (int Exit, string Output) RunPowerShell(string body)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var script = "$ErrorActionPreference = 'Stop'\n. '" + Lib.Replace("'", "''", StringComparison.Ordinal) + "'\n" + body;
        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-units-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell не завершився за 120 с.");
            }

            return (process.ExitCode, stdout + stderr);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private const string Sample = """
        $t = "rows from T1;`n`nconst k_M : one = 0.5;`nconst q_M : one = 42;`n`noutput O1 : t = row.Rho * k_M * row.RhoExtra;`noutput O2 : t = row.Rho / q_M;`n"
        """;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Аргумент_стає_змінною_з_одиницею_і_замінюється_у_виразах()
    {
        var (exit, output) = RunPowerShell(Sample + """

            $r = Set-LandScriptUnits -Text $t -Arguments @{ Rho = 'kg_per_Sm3' }
            $r
            """);
        Assert.Equal(0, exit);
        Assert.Contains("var u_Rho : kg_per_Sm3 = row.Rho;", output, StringComparison.Ordinal);
        Assert.Contains("output O2 : t = u_Rho / q_M;", output, StringComparison.Ordinal);
        Assert.Contains("u_Rho * k_M * row.RhoExtra", output, StringComparison.Ordinal);
        Assert.True(
            output.IndexOf("var u_Rho", StringComparison.Ordinal) < output.IndexOf("output O1", StringComparison.Ordinal),
            "оголошення має стояти перед першим output");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Аргумент_із_спільним_початком_імені_не_чіпається()
    {
        var (exit, output) = RunPowerShell(Sample + """

            Set-LandScriptUnits -Text $t -Arguments @{ Rho = 'kg_per_Sm3' }
            """);
        Assert.Equal(0, exit);
        Assert.Contains("row.RhoExtra", output, StringComparison.Ordinal);
        Assert.DoesNotContain("u_RhoExtra", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Константа_міняє_одиницю_а_число_лишається()
    {
        var (exit, output) = RunPowerShell(Sample + """

            Set-LandScriptUnits -Text $t -Constants @{ q_M = 'MJ_per_kg' }
            """);
        Assert.Equal(0, exit);
        Assert.Contains("const q_M : MJ_per_kg = 42;", output, StringComparison.Ordinal);
        Assert.Contains("const k_M : one = 0.5;", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Відсутній_аргумент_чи_константа_дає_помилку()
    {
        var (exit1, output1) = RunPowerShell(Sample + "\nSet-LandScriptUnits -Text $t -Arguments @{ Missing = 'kg' }");
        Assert.NotEqual(0, exit1);
        Assert.Contains("Missing", output1, StringComparison.Ordinal);

        var (exit2, output2) = RunPowerShell(Sample + "\nSet-LandScriptUnits -Text $t -Constants @{ zz = 'kg' }");
        Assert.NotEqual(0, exit2);
        Assert.Contains("zz", output2, StringComparison.Ordinal);
    }
}
