using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-07: <c>tools/land/Import-LandAirSubstances.ps1</c> (довідник LAND_AIR_SUBSTANCE зі старої системи) —
/// чиста логіка плану на синтетичних рядках і запобіжники (локальний API, локальне підтверджене джерело,
/// лише SELECT … WITH (NOLOCK), мутації лише з -ConfirmStand). Значень прод-даних у тестах немає.
/// </summary>
public sealed class LandAirSubstancesScriptTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private static (int Exit, string Output) RunPowerShell(string script, params string[] args)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // -File, а не -Command: аргументи з `;` і лапками не мають розбиратися вдруге.
        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-air-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }
                         .Concat(args))
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

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_бере_коди_з_3_4_цифр_знімає_провідні_нулі_і_лічить_пропущене()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            . $args[1]
            $rows = @(
              [pscustomobject]@{ Code = '301';  NameEn = 'Alpha'; NameRu = 'Alpha-ru'; Formula = 'X1'; StateEn = 'gas'; StateRu = 'gas-ru' },
              [pscustomobject]@{ Code = '0304'; NameEn = '';      NameRu = 'Beta-ru';  Formula = $null; StateEn = $null; StateRu = $null },
              [pscustomobject]@{ Code = '301';  NameEn = 'Dup';   NameRu = '';      Formula = '';  StateEn = '';  StateRu = '' },
              [pscustomobject]@{ Code = 'abc';  NameEn = 'Text';  NameRu = '';      Formula = '';  StateEn = '';  StateRu = '' },
              [pscustomobject]@{ Code = '12';   NameEn = 'Short'; NameRu = '';      Formula = '';  StateEn = '';  StateRu = '' },
              [pscustomobject]@{ Code = '99999'; NameEn = 'Long'; NameRu = '';      Formula = '';  StateEn = '';  StateRu = '' },
              [pscustomobject]@{ Code = $null;  NameEn = 'Null';  NameRu = '';      Formula = '';  StateEn = '';  StateRu = '' },
              [pscustomobject]@{ Code = '2754'; NameEn = ('N' * 300); NameRu = ''; Formula = '';  StateEn = '';  StateRu = '' }
            )
            $plan = ConvertTo-LandAirSubstancePlan -Rows $rows
            'ITEMS=' + (@($plan.Items | ForEach-Object { $_.EntryCode }) -join ',')
            'SKIPPED=' + $plan.Skipped
            'DUPS=' + $plan.Duplicates
            'NORM=' + $plan.Normalized
            'BETA=' + (@($plan.Items | Where-Object { $_.Code -eq '304' } | ForEach-Object { $_.DisplayEn + '|' + $_.DisplayRu }) -join '')
            'LONG=' + (@($plan.Items | Where-Object { $_.Code -eq '2754' } | ForEach-Object { $_.DisplayEn.Length }) -join '')
            'EMPTY=' + (@((ConvertTo-LandAirSubstancePlan -Rows @()).Items).Count)
            """;

        var (exit, output) = RunPowerShell(script, LandFile("EcrApi.ps1"), LandFile("LandAirSubstances.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("ITEMS=E301,E304,E2754", output, StringComparison.Ordinal);
        Assert.Contains("SKIPPED=4", output, StringComparison.Ordinal);
        Assert.Contains("DUPS=1", output, StringComparison.Ordinal);
        Assert.Contains("NORM=1", output, StringComparison.Ordinal);
        Assert.Contains("BETA=Beta-ru|Beta-ru", output, StringComparison.Ordinal);
        Assert.Contains("LONG=200", output, StringComparison.Ordinal);
        Assert.Contains("EMPTY=0", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Джерело_допускається_лише_локальне_і_лише_з_підтвердженою_базою()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            . $args[1]
            function Probe($server, $db, $confirm) {
              try { Assert-LocalSourceDatabase -Server $server -Database $db -ConfirmSource $confirm; "OK|$server|$db|$confirm" }
              catch { "REFUSED|$server|$db|$confirm" }
            }
            Probe 'localhost' 'CopyDb' 'CopyDb'
            Probe '.\SQLEXPRESS' 'CopyDb' 'copydb'
            Probe 'tcp:127.0.0.1,1433' 'CopyDb' 'CopyDb'
            Probe 'sql01.corp.example.com' 'CopyDb' 'CopyDb'
            Probe '10.1.2.3' 'CopyDb' 'CopyDb'
            Probe 'localhost.evil.com' 'CopyDb' 'CopyDb'
            Probe 'localhost' 'CopyDb' ''
            Probe 'localhost' 'CopyDb' 'Other'
            Probe 'localhost' 'Bad;Db' 'Bad;Db'
            Probe 'localhost' '' ''
            """;

        var (exit, output) = RunPowerShell(script, LandFile("EcrApi.ps1"), LandFile("LandAirSubstances.Lib.ps1"));
        Assert.True(exit == 0, output);

        foreach (var ok in new[] { "OK|localhost|CopyDb|CopyDb", @"OK|.\SQLEXPRESS|CopyDb|copydb", "OK|tcp:127.0.0.1,1433|CopyDb|CopyDb" })
        {
            Assert.Contains(ok, output, StringComparison.Ordinal);
        }

        foreach (var refused in new[]
                 {
                     "REFUSED|sql01.corp.example.com|CopyDb|CopyDb", "REFUSED|10.1.2.3|CopyDb|CopyDb",
                     "REFUSED|localhost.evil.com|CopyDb|CopyDb", "REFUSED|localhost|CopyDb|",
                     "REFUSED|localhost|CopyDb|Other", "REFUSED|localhost|Bad;Db|Bad;Db", "REFUSED|localhost||",
                 })
        {
            Assert.Contains(refused, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_відмовляє_до_будь_якого_читання_на_чужому_API_чужому_джерелі_і_без_підтверджень()
    {
        var import = LandFile("Import-LandAirSubstances.ps1");

        // Чужий API — відмова, навіть із підтвердженнями й -DryRun.
        var (remoteExit, remote) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'https://ecr.example.com' -SourceDatabase CopyDb -ConfirmSource CopyDb -ConfirmStand 'ecr.example.com' -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, remoteExit);
        Assert.Contains("localhost", remote, StringComparison.Ordinal);

        // Чужий SQL-сервер джерела.
        var (srvExit, srv) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094' -SourceServer 'sql01.corp.example.com' -SourceDatabase CopyDb -ConfirmSource CopyDb -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, srvExit);
        Assert.Contains("sql01.corp.example.com", srv, StringComparison.Ordinal);

        // Немає -ConfirmSource.
        var (noConfirmExit, noConfirm) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094' -SourceDatabase CopyDb -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, noConfirmExit);
        Assert.Contains("-ConfirmSource", noConfirm, StringComparison.Ordinal);

        // Мутація (без -DryRun) без -ConfirmStand — відмова з назвою параметра ДО читання джерела.
        var (standExit, stand) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094' -SourceDatabase CopyDb -ConfirmSource CopyDb; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, standExit);
        Assert.Contains("-ConfirmStand", stand, StringComparison.Ordinal);

        // Додавання до версії: усі три параметри або жоден.
        var (attachExit, attach) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094' -SourceDatabase CopyDb -ConfirmSource CopyDb -AttachMethodologyId 5 -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, attachExit);
        Assert.Contains("-AttachVersionId", attach, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Текст_скрипта_читає_лише_SELECT_NOLOCK_і_не_має_значень_а_запобіжники_стоять_до_підключення()
    {
        var text = File.ReadAllText(LandFile("Import-LandAirSubstances.ps1"));

        Assert.Contains("[string]$ConfirmStand", text, StringComparison.Ordinal);
        Assert.Contains("Set-EcrStandConfirmation", text, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalBaseUrl", text, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalSourceDatabase", text, StringComparison.Ordinal);
        Assert.Contains("ApplicationIntent", text, StringComparison.Ordinal);

        var sourceGuard = text.IndexOf("Assert-LocalSourceDatabase -Server", StringComparison.Ordinal);
        var apiGuard = text.IndexOf("Assert-LocalBaseUrl $BaseUrl", StringComparison.Ordinal);
        var open = text.IndexOf("$conn.Open()", StringComparison.Ordinal);
        var login = text.IndexOf("Connect-Ecr -BaseUrl", StringComparison.Ordinal);
        Assert.True(apiGuard >= 0 && apiGuard < open, "Локальність API перевіряється до читання джерела.");
        Assert.True(sourceGuard >= 0 && sourceGuard < open, "Підтвердження джерела — до підключення.");
        Assert.True(open < login, "Спершу читання джерела, потім вхід у стенд.");

        // Єдиний запит до джерела — SELECT з NOLOCK; жодних DML.
        var command = Regex.Match(text, @"CommandText\s*=\s*(?<sql>.*?)\r?\n\s*\$rd\s*=", RegexOptions.Singleline);
        Assert.True(command.Success, "Запит до джерела не знайдено.");
        var sql = command.Groups["sql"].Value;
        Assert.Contains("WITH (NOLOCK)", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT", sql, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|TRUNCATE|EXEC)\b", sql);

        // Значень прод-даних у git немає: у файлах жодного числового коду речовини 3-4 цифри в лапках.
        foreach (var name in new[] { "Import-LandAirSubstances.ps1", "LandAirSubstances.Lib.ps1" })
        {
            Assert.DoesNotMatch(@"['""][1-9][0-9]{2,3}['""]", File.ReadAllText(LandFile(name)));
        }
    }
}
