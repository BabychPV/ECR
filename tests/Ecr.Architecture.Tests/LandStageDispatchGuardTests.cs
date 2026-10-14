// tests/Ecr.Architecture.Tests/LandStageDispatchGuardTests.cs
using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторож диспетчеризації етапів <c>tools/land/Build-LandTemplate.ps1</c>: КОЖНЕ значення <c>ValidateSet</c>
/// параметра <c>-Stage</c> (крім <c>All</c>) має мати справжній <c>IfStatementAst</c> із умовою за іменем етапу
/// (<c>Want 'X'</c> або <c>$Stage -contains 'X'</c>) і <c>CommandAst</c> виклику <c>Stage-X</c> у його тілі. Перевірка
/// йде по AST, а не по тексту: рядок, що «злився» в коментар (літерал <c>\n</c> замість переводу рядка, LAND-5094),
/// збігається з regex-ом, але виконуватись не буде - <c>-Stage X</c> тихо нічого не робить, хоч ValidateSet його пропускає.
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути літерал <c>\n</c> між коментарем і <c>if</c> у діспетчері будь-якого етапу -
/// <see cref="Кожен_етап_із_ValidateSet_має_AST_диспетчер_зі_своїм_Stage_викликом"/> червоніє з переліком етапів.
/// </remarks>
public sealed class LandStageDispatchGuardTests
{
    private const string Harness = """
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $ErrorActionPreference = 'Stop'
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$null, [ref]$null)
        $p = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Stage' } | Select-Object -First 1
        if (-not $p) { throw 'параметр Stage не знайдено' }
        $vs = $p.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateSet' } | Select-Object -First 1
        if (-not $vs) { throw 'ValidateSet для Stage не знайдено' }
        $values = @($vs.PositionalArguments | ForEach-Object { [string]$_.Value })
        $ifs = @($ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.IfStatementAst] }, $true))
        $fns = @($ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object { $_.Name })
        foreach ($v in $values) {
            $offset = -1
            foreach ($if in $ifs) {
                foreach ($clause in $if.Clauses) {
                    $names = @($clause.Item1.FindAll({ param($a) $a -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $a.Value -ceq $v }, $true))
                    if ($names.Count -eq 0) { continue }
                    $calls = @($clause.Item2.FindAll({ param($a) $a -is [System.Management.Automation.Language.CommandAst] -and $a.GetCommandName() -ceq ('Stage-' + $v) }, $true))
                    if ($calls.Count -gt 0) { $offset = $calls[0].Extent.StartOffset }
                }
            }
            $hasFn = $fns -ccontains ('Stage-' + $v)
            Write-Host ('STAGE=' + $v + '|' + $offset + '|' + [int]$hasFn)
        }
        """;

    private sealed record StageInfo(string Name, int Offset, bool HasFunction);

    internal static string BuildScript => Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1");

    /// <summary>Етапи з ValidateSet і зміщення виклику Stage-* у диспетчері (-1 - диспетчера немає).</summary>
    internal static IReadOnlyDictionary<string, int> DispatchOffsets() =>
        Analyse().ToDictionary(s => s.Name, s => s.Offset, StringComparer.Ordinal);

    private static List<StageInfo> Analyse()
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-stagedispatch-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, Harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file, BuildScript })
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

            Assert.True(process.ExitCode == 0, stdout + stderr);
            var result = new List<StageInfo>();
            foreach (var raw in stdout.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("STAGE=", StringComparison.Ordinal))
                {
                    continue;
                }

                var parts = line["STAGE=".Length..].Split('|');
                result.Add(new StageInfo(parts[0], int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), parts[2] == "1"));
            }

            return result;
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_етап_із_ValidateSet_має_AST_диспетчер_зі_своїм_Stage_викликом()
    {
        var stages = Analyse().Where(s => s.Name != "All").ToList();
        Assert.NotEmpty(stages);

        var noDispatch = stages.Where(s => s.Offset < 0).Select(s => s.Name).ToList();
        var noFunction = stages.Where(s => !s.HasFunction).Select(s => s.Name).ToList();

        Assert.True(
            noDispatch.Count == 0,
            "-Stage приймає значення, для яких немає виконуваного диспетчера `if (Want 'X' | $Stage -contains 'X') { Stage-X }` "
            + "(у AST немає IfStatementAst з викликом; типова причина - літерал \\n замість переводу рядка, і `if` став частиною коментаря): "
            + string.Join(", ", noDispatch));
        Assert.True(noFunction.Count == 0, "Немає функції Stage-X для: " + string.Join(", ", noFunction));
    }
}
