using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Етап <c>LegacyPublish</c> (Build-LandTemplate.ps1): золотий знімок виходів ПО РЕЧОВИНАХ. Чиста частина —
/// <c>tools/land/LandLegacyPublish.Lib.ps1</c>; СИНТЕТИЧНІ відповіді (вигадані коди виходів і числа), без мережі й БД.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): у <c>New-LandSnapshotExpected</c> замінити ключ <c>'{0}@{1}'</c> на <c>[string]$a.Output</c> —
/// червоніють <c>Поречовинний_вихід_дає_ключ_код_речовина_а_не_одне_число_на_код</c> і
/// <c>Текстова_відповідь_теж_несе_речовину</c>; у <c>Get-LandGoldenActuals</c> прибрати пропуск порожнього
/// <c>actual</c> — червоніє <c>Вихід_якого_не_було_у_знімок_не_потрапляє</c>; у <c>Get-LandGoldenRowColumn</c> прибрати гілку
/// суфіксу (<c>__ECW_</c>) — червоніє <c>Суфіксний_аргумент_читає_базову_колонку_рядка_а_власна_колонка_має_перевагу</c>; так само
/// без псевдоніму <c>IDCodeNAEProject</c> → <c>Land_NAE</c>; у <c>Get-LandGoldenRowColumn</c> послабити регулярний вираз
/// <c>[X]</c> (прибрати <c>$</c>) — червоніє той самий тест (<c>I=True</c>); у <c>Test-LandGoldenBridgeArgument</c> замінити
/// <c>LAND_HSE400_COMPOSITION</c> на інший текст — червоніє <c>Мостова_колонка_складу_HSE400_не_робить_таблицю_неповною_а_звичайна_відсутня_робить</c>;
/// у <c>Get-LandGoldenLookupRegistry</c> прибрати гілку місячної формули (повертати <c>$null</c> після Lookup) — червоніє
/// <c>Довідник_колонки_формули_вибору_місяця_береться_з_місячної_Lookup_колонки</c>.
/// </remarks>
public sealed class LandLegacyPublishLibTests
{
    private static string Lib => Path.Combine(SourceTree.Root, "tools", "land", "LandLegacyPublish.Lib.ps1");

    private static (int Exit, string Output) RunPowerShell(string script)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-lpub-{Guid.NewGuid():N}.ps1");
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

    private static string Script(string body)
        => "$ErrorActionPreference = 'Stop'\n. '" + Lib.Replace("'", "''", StringComparison.Ordinal) + "'\n" + body;

    private const string Structured = """
        $json = '{"errorCode":"ECR-CALC-0422","goldenSet":[{"code":"SNAP_1","isGreen":false,"mismatches":[
          {"outputCode":"OUT_TONS","substanceEntryId":11,"actual":0.5,"expected":-987654321,"tolerance":1e-9},
          {"outputCode":"OUT_TONS","substanceEntryId":12,"actual":2.25,"expected":-987654321,"tolerance":1e-9},
          {"outputCode":"OUT_GSEC","substanceEntryId":null,"actual":7,"expected":-987654321,"tolerance":1e-9},
          {"outputCode":"OUT_BRANCH","substanceEntryId":null,"actual":null,"expected":-987654321,"tolerance":1e-9}]}]}' | ConvertFrom-Json
        """;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Поречовинний_вихід_дає_ключ_код_речовини_а_не_одне_число_на_код()
    {
        var script = Script(Structured + """

            $a = @(Get-LandGoldenActuals -Json $json)
            $e = New-LandSnapshotExpected -Actuals $a -Outputs @('OUT_TONS', 'OUT_GSEC', 'OUT_BRANCH')
            'COUNT=' + $e.Count
            'K=' + (($e.Keys | ForEach-Object { $_ + ':' + $e[$_] }) -join '|')
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("COUNT=3", output, StringComparison.Ordinal);
        Assert.Contains("K=OUT_TONS@11:0.5|OUT_TONS@12:2.25|OUT_GSEC:7", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Вихід_якого_не_було_у_знімок_не_потрапляє()
    {
        var script = Script(Structured + """

            $a = @(Get-LandGoldenActuals -Json $json)
            'N=' + $a.Count
            'HAS_BRANCH=' + (@($a | Where-Object { $_.Output -eq 'OUT_BRANCH' }).Count)
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("N=3", output, StringComparison.Ordinal);
        Assert.Contains("HAS_BRANCH=0", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Текстова_відповідь_теж_несе_речовину()
    {
        var script = Script("""
            $text = "SNAP_1: OUT_TONS (речовина 11): очікували -987654321 ± 1e-9, отримали 0,5; SNAP_1: OUT_TONS (речовина 12): очікували -987654321 ± 1e-9, отримали 2.25; SNAP_1: OUT_GSEC (без речовини): очікували -987654321 ± 1e-9, отримали 7; SNAP_1: OUT_BRANCH (без речовини): очікували -987654321 ± 1e-9, отримали виходу не було"
            $a = @(Get-LandGoldenActuals -Text $text)
            $e = New-LandSnapshotExpected -Actuals $a -Outputs @('OUT_TONS', 'OUT_GSEC', 'OUT_BRANCH')
            'K=' + (($e.Keys | ForEach-Object { $_ + ':' + $e[$_] }) -join '|')
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("K=OUT_TONS@11:0.5|OUT_TONS@12:2.25|OUT_GSEC:7", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Неоголошений_вихід_і_порожня_відповідь_дають_порожнє_очікування()
    {
        var script = Script(Structured + """

            $a = @(Get-LandGoldenActuals -Json $json)
            $e = New-LandSnapshotExpected -Actuals $a -Outputs @('ІНШИЙ')
            'COUNT=' + $e.Count
            $none = @(Get-LandGoldenActuals -Json $null -Text '')
            'NONE=' + $none.Count
            $e2 = New-LandSnapshotExpected -Actuals $none -Outputs @('OUT_TONS')
            'COUNT2=' + $e2.Count
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("COUNT=0", output, StringComparison.Ordinal);
        Assert.Contains("NONE=0", output, StringComparison.Ordinal);
        Assert.Contains("COUNT2=0", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Суфіксний_аргумент_читає_базову_колонку_рядка_а_власна_колонка_має_перевагу()
    {
        var script = Script("""
            $row = @{ Land_Measure_Foo = 5; Land_Bar = 1; Land_Bar__ECW_C99_01 = 2 }
            'A=' + (Get-LandGoldenRowColumn -Argument 'Land_Measure_Foo__ECW_C99_01' -Row $row)
            'B=' + (Get-LandGoldenRowColumn -Argument 'Land_Bar__ECW_C99_01' -Row $row)
            'C=' + (Get-LandGoldenRowColumn -Argument 'Land_Bar' -Row $row)
            'D=' + ($null -eq (Get-LandGoldenRowColumn -Argument 'Land_Nope__ECW_C99_01' -Row $row))
            'E=' + ($null -eq (Get-LandGoldenRowColumn -Argument 'Land_Nope' -Row $row))
            'F=' + (Get-LandGoldenRowColumn -Argument 'IDCodeNAEProject' -Row @{ Land_NAE = '1' })
            'G=' + ($null -eq (Get-LandGoldenRowColumn -Argument 'IDCodeNAEProject' -Row @{ Land_X = '1' }))
            $cols = @([pscustomobject]@{ code = 'Land_Stream'; expression = ' [TypeX] ' }, [pscustomobject]@{ code = 'Land_Calc'; expression = '[TypeX] + 1' })
            'H=' + (Get-LandGoldenRowColumn -Argument 'Land_Stream' -Row @{ TypeX = 's' } -Columns $cols)
            'I=' + ($null -eq (Get-LandGoldenRowColumn -Argument 'Land_Calc' -Row @{ TypeX = 's' } -Columns $cols))
            'J=' + ($null -eq (Get-LandGoldenRowColumn -Argument 'Land_Stream' -Row @{ Other = 's' } -Columns $cols))
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("A=Land_Measure_Foo", output, StringComparison.Ordinal);
        Assert.Contains("B=Land_Bar__ECW_C99_01", output, StringComparison.Ordinal);
        Assert.Contains("C=Land_Bar", output, StringComparison.Ordinal);
        Assert.Contains("D=True", output, StringComparison.Ordinal);
        Assert.Contains("E=True", output, StringComparison.Ordinal);
        Assert.Contains("F=Land_NAE", output, StringComparison.Ordinal);
        Assert.Contains("G=True", output, StringComparison.Ordinal);
        Assert.Contains("H=TypeX", output, StringComparison.Ordinal);
        Assert.Contains("I=True", output, StringComparison.Ordinal);
        Assert.Contains("J=True", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Мостова_колонка_складу_HSE400_не_робить_таблицю_неповною_а_звичайна_відсутня_робить()
    {
        var script = Script("""
            $cols = @(
                [pscustomobject]@{ code = 'Density20'; expression = "REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', 1, 1, 'X', 'Density20'), 'VALUE')" },
                [pscustomobject]@{ code = 'Other'; expression = '[A] + 1' })
            $row = @{ A = 1; Land_NAE = '1' }
            $m = @(Get-LandGoldenMissingArguments -Arguments @('Density20', 'Other', 'Land_NAE', 'Gone') -Row $row -Columns $cols)
            'M=' + ($m -join ',')
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("M=Other,Gone", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Довідник_колонки_формули_вибору_місяця_береться_з_місячної_Lookup_колонки()
    {
        var script = Script("""
            $cols = @(
                [pscustomobject]@{ code = 'Val_M01'; dataType = 'Lookup'; registry = 'REG_A'; expression = $null },
                [pscustomobject]@{ code = 'Val_M02'; dataType = 'Lookup'; registry = 'REG_A'; expression = $null },
                [pscustomobject]@{ code = 'Pick'; dataType = 'Formula'; registry = $null; expression = '[Period].Sequence = 1 ? [Val_M01] : [Val_M02]' },
                [pscustomobject]@{ code = 'Direct'; dataType = 'Lookup'; registry = 'REG_B'; expression = $null },
                [pscustomobject]@{ code = 'Calc'; dataType = 'Formula'; registry = $null; expression = '[A] + [B]' },
                [pscustomobject]@{ code = 'TextM'; dataType = 'Formula'; registry = $null; expression = '[Txt_M01]' },
                [pscustomobject]@{ code = 'Txt_M01'; dataType = 'String'; registry = $null; expression = $null })
            $by = @{}; foreach ($c in $cols) { $by[$c.code] = $c }
            'A=' + (Get-LandGoldenLookupRegistry -Column $by['Pick'] -Columns $cols)
            'B=' + (Get-LandGoldenLookupRegistry -Column $by['Direct'] -Columns $cols)
            'C=' + ($null -eq (Get-LandGoldenLookupRegistry -Column $by['Calc'] -Columns $cols))
            'D=' + ($null -eq (Get-LandGoldenLookupRegistry -Column $by['TextM'] -Columns $cols))
            'E=' + ($null -eq (Get-LandGoldenLookupRegistry -Column $null -Columns $cols))
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("A=REG_A", output, StringComparison.Ordinal);
        Assert.Contains("B=REG_B", output, StringComparison.Ordinal);
        Assert.Contains("C=True", output, StringComparison.Ordinal);
        Assert.Contains("D=True", output, StringComparison.Ordinal);
        Assert.Contains("E=True", output, StringComparison.Ordinal);
    }
}
