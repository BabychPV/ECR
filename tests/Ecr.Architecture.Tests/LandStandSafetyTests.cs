using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// RC21 F: скрипти tools/land (а) працюють лише з локальним API і локальним SQL, (б) не виконують мутуючих
/// викликів без явного -ConfirmStand. Pester 5 тут немає, тож функції EcrApi.ps1 запускаються в дочірньому
/// PowerShell без мережі (лише перевірка адрес і прапорів), решта — статичні інваріанти тексту скриптів
/// за прецедентом <c>LandScriptsHttpContractTests</c>.
/// </summary>
public sealed class LandStandSafetyTests
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
        // ⚠ -File, а не -Command: Windows PowerShell склеює аргументи після -Command в текст команди й розбирає
        // його заново — рядок підключення з `;` розпадався б на кілька команд (і тест зависав).
        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-safety-{Guid.NewGuid():N}.ps1");
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

    private static Dictionary<string, string> Verdicts(string function, params string[] values)
    {
        // Кожне значення — окремий виклик у try/catch; у вихід іде лише ASCII-вердикт.
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            foreach ($v in $args[2..($args.Count - 1)]) {
              try { & $args[1] $v; "OK`t$v" } catch { "REFUSED`t$v" }
            }
            """;
        var (exit, output) = RunPowerShell(script, [LandFile("EcrApi.ps1"), function, .. values]);
        Assert.True(exit == 0, $"Скрипт впав: {output}");

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("OK\t", StringComparison.Ordinal) || l.StartsWith("REFUSED\t", StringComparison.Ordinal))
            .Select(l => l.Split('\t', 2))
            .ToDictionary(p => p[1], p => p[0], StringComparer.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Адреса_API_допускається_лише_локальна()
    {
        string[] allowed =
        [
            "http://localhost:5094", "http://127.0.0.1:5094", "http://[::1]:5094", "https://ecr.localhost:5001",
            "https://LOCALHOST",
        ];
        string[] refused =
        [
            "https://ecr.example.com", "http://10.0.0.5:5094", "http://localhost.evil.com", "http://127.0.0.2:5094",
            "https://evil.com/localhost", "not-a-url", "http://prod-ecr:5094",
        ];

        var result = Verdicts("Assert-LocalBaseUrl", [.. allowed, .. refused]);

        Assert.All(allowed, a => Assert.Equal("OK", result[a]));
        Assert.All(refused, r => Assert.Equal("REFUSED", result[r]));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void SQL_сервер_допускається_лише_локальний()
    {
        string[] allowed =
        [
            "Server=localhost;Database=EcrLandUi;Integrated Security=true",
            @"Server=.\SQLEXPRESS;Database=X;Integrated Security=true",
            "Server=(local);Database=X;Integrated Security=true",
            "Server=tcp:127.0.0.1,1433;Database=X;Integrated Security=true",
            "Server=[::1];Database=X;Integrated Security=true",
            @"Data Source=(localdb)\MSSQLLocalDB;Initial Catalog=X;Integrated Security=true",
            @"Server=LOCALHOST\SQL2022,1433;Database=X;Integrated Security=true",
        ];
        string[] refused =
        [
            "Server=sql01.corp.example.com;Database=X;Integrated Security=true",
            "Server=10.1.2.3;Database=X;Integrated Security=true",
            "Server=localhost.evil.com;Database=X;Integrated Security=true",
            @"Data Source=prod\inst;Initial Catalog=X;Integrated Security=true",
            "Server=tcp:db.example.com,1433;Database=X;User Id=sa;Password=x",
            "Database=X;Integrated Security=true",
        ];

        var result = Verdicts("Assert-LocalSqlConnection", [.. allowed, .. refused]);

        Assert.All(allowed, a => Assert.Equal("OK", result[a]));
        Assert.All(refused, r => Assert.Equal("REFUSED", result[r]));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Мутуючий_виклик_без_ConfirmStand_відмовляє_до_мережі_а_хибне_підтвердження_не_вмикає_нічого()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            try { Invoke-Ecr -Method POST -Path '/api/v1/roles' -Body @{ code = 'x' }; 'NOGUARD' } catch { 'GUARDED:' + $_.Exception.Message }
            try { Assert-EcrMutationAllowed 'probe'; 'NOGUARD' } catch { 'GUARDED2' }
            try { Set-EcrStandConfirmation -Stand 'other-host:1' -Url 'http://localhost:5094'; 'CONFIRMED-WRONG' } catch { 'WRONG-REFUSED' }
            try { Set-EcrStandConfirmation -Stand '' -Url 'http://localhost:5094'; 'CONFIRMED-EMPTY' } catch { 'EMPTY-REFUSED' }
            try { Set-EcrStandConfirmation -Stand 'localhost:5094' -Url 'https://prod.example.com'; 'CONFIRMED-REMOTE' } catch { 'REMOTE-REFUSED' }
            try { Assert-EcrMutationAllowed 'probe'; 'NOGUARD2' } catch { 'STILL-GUARDED' }
            Set-EcrStandConfirmation -Stand 'localhost:5094' -Url 'http://localhost:5094'
            try { Assert-EcrMutationAllowed 'probe'; 'ALLOWED' } catch { 'BLOCKED' }
            """;
        var (exit, output) = RunPowerShell(script, LandFile("EcrApi.ps1"));
        Assert.True(exit == 0, output);

        Assert.Contains("GUARDED:", output, StringComparison.Ordinal);
        Assert.Contains("-ConfirmStand", output, StringComparison.Ordinal);
        Assert.DoesNotContain("NOGUARD", output, StringComparison.Ordinal);
        Assert.Contains("GUARDED2", output, StringComparison.Ordinal);
        Assert.Contains("WRONG-REFUSED", output, StringComparison.Ordinal);
        Assert.Contains("EMPTY-REFUSED", output, StringComparison.Ordinal);
        Assert.Contains("REMOTE-REFUSED", output, StringComparison.Ordinal);
        Assert.Contains("STILL-GUARDED", output, StringComparison.Ordinal);
        Assert.Contains("ALLOWED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("CONFIRMED-", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_LandTemplate_відмовляє_на_чужому_API_і_без_ConfirmStand_ще_до_читання_моделі()
    {
        var build = LandFile("Build-LandTemplate.ps1");

        // Чужий API — відмова навіть із -DryRun і з -ConfirmStand.
        var (remoteExit, remote) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'https://ecr.example.com' -ConfirmStand 'ecr.example.com' -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            build);
        Assert.Equal(7, remoteExit);
        Assert.Contains("localhost", remote, StringComparison.Ordinal);

        // Чужий SQL — відмова.
        var (sqlExit, sql) = RunPowerShell(
            "try { & $args[0] -SqlConnection 'Server=sql01.corp.example.com;Database=X;Integrated Security=true' -ConfirmStand 'localhost:5094' -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            build);
        Assert.Equal(7, sqlExit);
        Assert.Contains("SQL", sql, StringComparison.Ordinal);

        // Локальний API, але без -ConfirmStand і без -DryRun — відмова з назвою параметра.
        var (confirmExit, confirm) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094' -Stage Units; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            build);
        Assert.Equal(7, confirmExit);
        Assert.Contains("-ConfirmStand", confirm, StringComparison.Ordinal);

        // -DryRun без підтвердження дозволений (лише читання моделі).
        var (dryExit, dry) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094' -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            build);
        Assert.True(dryExit == 0, dry);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Усі_мутуючі_скрипти_мають_ConfirmStand_і_SQL_записи_під_захистом()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));
        var apply = File.ReadAllText(LandFile("Apply-ContractHeader.ps1"));
        var import = File.ReadAllText(LandFile("Import-ContractDictionaries.ps1"));

        foreach (var (name, text) in new[] { ("Build", build), ("Apply", apply), ("Import", import) })
        {
            Assert.True(text.Contains("[string]$ConfirmStand", StringComparison.Ordinal), $"{name}: немає параметра -ConfirmStand.");
            Assert.True(text.Contains("Set-EcrStandConfirmation", StringComparison.Ordinal), $"{name}: не вмикає підтвердження.");
            Assert.True(text.Contains("Assert-LocalBaseUrl", StringComparison.Ordinal), $"{name}: не перевіряє локальність API.");
        }

        // Дочірні скрипти отримують підтвердження від Build (інакше їхні мутації відмовили б).
        Assert.Contains("Import-ContractDictionaries.ps1') -BaseUrl $BaseUrl -User 'land-owner' -Password $pw -ConfirmStand $ConfirmStand", build, StringComparison.Ordinal);
        Assert.Contains("Apply-ContractHeader.ps1') -TemplateCode $TemplateCode -BaseUrl $BaseUrl -User 'land-owner' -Password $pw -ConfirmStand $ConfirmStand", build, StringComparison.Ordinal);

        // SQL-запис: кожна точка входу сама перевіряє підтвердження (не лише верхній рівень).
        foreach (var fn in new[] { "function Sql-Exec", "function Bulk-Insert", "function Fill-StructureSql" })
        {
            var at = build.IndexOf(fn, StringComparison.Ordinal);
            Assert.True(at >= 0, fn);
            Assert.Contains("Assert-EcrMutationAllowed", build.Substring(at, 400), StringComparison.Ordinal);
        }

        Assert.Contains("Assert-LocalSqlConnection $SqlConnection", build, StringComparison.Ordinal);

        // Усі не-GET через Invoke-Ecr проходять одну точку: guard стоїть у ній, до HttpClient.
        var api = File.ReadAllText(LandFile("EcrApi.ps1"));
        var invoke = api.IndexOf("function Invoke-Ecr", StringComparison.Ordinal);
        Assert.True(invoke >= 0);
        var guard = api.IndexOf("Assert-EcrMutationAllowed \"$Method $Path\"", invoke, StringComparison.Ordinal);
        var client = api.IndexOf("New-Object System.Net.Http.HttpClientHandler", invoke, StringComparison.Ordinal);
        Assert.True(guard > invoke && guard < client, "Guard мутацій має стояти в Invoke-Ecr до створення HttpClient.");

        // Connect-Ecr не віддає ні логін, ні cookie не-локальному серверу.
        var connect = api.IndexOf("function Connect-Ecr", StringComparison.Ordinal);
        var login = api.IndexOf("'/api/v1/login/local'", connect, StringComparison.Ordinal);
        var local = api.IndexOf("Assert-LocalBaseUrl $BaseUrl", connect, StringComparison.Ordinal);
        Assert.True(local > connect && local < login, "Перевірка локальності має бути до POST /login/local.");
    }
}
