using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-474, рішення людини 11.11 (шаблон Land 1.0.16.0): версія Land будується з розкладкою <c>YearWithMonthColumns</c> —
/// у документі немає перемикача місяців. Це можливо лише тоді, коли КОЖНА таблиця моделі або з місяцями в колонках
/// (<c>MonthsInColumns</c>), або без місяців (<c>Static</c>); розкладку ставить <c>Stage-Template</c> на чернетці до публікації
/// (після публікації сервер відмовляє: <c>409 ECR-SCHM-0409</c>).
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у моделі поставити таблиці <c>layout = PerPeriodInstance</c>; у <c>Get-LandPerPeriodTables</c>
/// додати <c>PerPeriodInstance</c> до дозволених; у Build перенести PUT layout після <c>Apply-ContractHeader</c> або змінити
/// типове значення <c>-Layout</c> — відповідні тести червоніють.
/// </remarks>
public sealed class LandYearLayoutTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Модель_Land_не_має_таблиць_копія_на_період()
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

        var layouts = model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray()
                .Select(t => (Table: $"{s.GetProperty("code").GetString()}/{t.GetProperty("code").GetString()}", Layout: t.GetProperty("layout").GetString())))
            .ToList();

        Assert.NotEmpty(layouts);
        Assert.Empty(layouts.Where(t => t.Layout is not ("MonthsInColumns" or "Static")).Select(t => $"{t.Table}={t.Layout}"));

        // без місячних таблиць річна розкладка не мала б чого показувати
        Assert.Contains(layouts, t => t.Layout == "MonthsInColumns");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сторож_розкладки_називає_таблиці_не_MonthsInColumns_і_не_Static()
    {
        const string harness = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            . $args[0]
            $mdl = '{"sheets":[{"code":"S1","tables":[{"code":"A","layout":"MonthsInColumns"},{"code":"B","layout":"Static"},{"code":"C","layout":"PerPeriodInstance"}]},
                               {"code":"S2","tables":[{"code":"D","layout":"MonthsInRows"},{"code":"E","layout":"monthsincolumns"}]}]}' | ConvertFrom-Json
            Write-Host ('BAD=' + (@(Get-LandPerPeriodTables $mdl) -join ','))
            $ok = '{"sheets":[{"code":"S1","tables":[{"code":"A","layout":"MonthsInColumns"},{"code":"B","layout":"Static"}]}]}' | ConvertFrom-Json
            Write-Host ('OK=' + @(Get-LandPerPeriodTables $ok).Count)
            """;

        var output = Run(harness, LandFile("LandTemplateDiff.Lib.ps1"));

        // регістр значення важить: сервер приймає лише точні назви TableLayoutKind
        Assert.Contains("BAD=S1/C=PerPeriodInstance,S2/D=MonthsInRows,S2/E=monthsincolumns\n", output, StringComparison.Ordinal);
        Assert.Contains("OK=0\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Збірка_ставить_річну_розкладку_на_чернетці_до_публікації()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));

        // типово - рік із місяцями в колонках; PerMonthCopies лише явним параметром
        Assert.Contains(
            "[ValidateSet('YearWithMonthColumns', 'PerMonthCopies')][string]$Layout = 'YearWithMonthColumns'",
            build,
            StringComparison.Ordinal);

        var stage = build.IndexOf("function Stage-Template {", StringComparison.Ordinal);
        var guard = build.IndexOf("$perPeriod = @(Get-LandPerPeriodTables $Mdl)", stage, StringComparison.Ordinal);
        var put = build.IndexOf(
            "Invoke-Ecr -Method PUT -Path \"/api/v1/template-versions/$vid/layout\" -Body @{ layout = $Layout }", stage, StringComparison.Ordinal);
        var check = build.IndexOf("Assert-EcrOk $lay", stage, StringComparison.Ordinal);

        // публікує версію Apply-ContractHeader (без -NoPublish) - розкладка мусить стати раніше
        var publish = build.IndexOf("'Apply-ContractHeader.ps1') -TemplateCode $TemplateCode", stage, StringComparison.Ordinal);

        Assert.True(stage > 0 && guard > stage, "сторож таблиць «копія на період» - у Stage-Template");
        Assert.True(put > guard, "PUT layout - після сторожа");
        Assert.True(check > put, "відмова сервера на PUT layout зупиняє збірку");
        Assert.True(publish > check, "PUT layout - ДО публікації версії (після неї 409 ECR-SCHM-0409)");
    }

    private static string Run(string harness, params string[] arguments)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-yearlayout-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }.Concat(arguments))
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
            return stdout.Replace("\r", string.Empty);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
