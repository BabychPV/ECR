using System.Diagnostics;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// I2-2: <c>deploy-ecr.ps1</c> ставить службу EcrWorker типово (крім Express і
/// <c>-DisableWorker</c>) і пише режим перерахунку Api за ФАКТОМ служби.
/// </summary>
/// <remarks>
/// ⛔ Предмет — справжні функції скрипта, не їх копія: тест вирізає визначення
/// <c>Resolve-WorkerDeployment</c>, <c>Resolve-JobExecutionConfig</c>,
/// <c>Remove-ServiceEnvironmentEntry</c> і <c>Get-ConfiguredValue</c> з
/// <c>tools/deploy-ecr.ps1</c> парсером PowerShell (AST) і виконує їх у
/// <c>pwsh</c> (Linux-раннер CI) або <c>powershell.exe</c> (Windows без pwsh).
/// Реєстру, msiexec і SQL тут немає — це перевіряється лише на реальній
/// установці (<c>verify-msi.ps1</c> без <c>-StaticOnly</c>).
/// <para>
/// ⚠ Скрипт-обгортка — лише ASCII: Windows PowerShell 5.1 читає файл без BOM
/// у кодовій сторінці ANSI, а вивід консолі — теж; кирилиця тут дала б
/// хибний збій, не пов'язаний із логікою.
/// </para>
/// </remarks>
public sealed class DeployWorkerModeTests
{
    private const string Harness = """
        param([string] $Deploy, [string] $Json, [string] $Placeholder, [string] $Commented)
        $ErrorActionPreference = 'Stop'
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($Deploy, [ref] $tokens, [ref] $errors)
        if ($errors.Count) { throw "parse: $($errors[0].Message)" }
        foreach ($name in 'Resolve-WorkerDeployment', 'Resolve-JobExecutionConfig', 'Remove-ServiceEnvironmentEntry', 'Get-ConfiguredValue', 'ConvertFrom-JsoncFile', 'Test-ConfigIsPlaceholder', 'Get-ConfiguredEditionMode') {
            $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
            if (-not $fn) { throw "no function $name" }
            . ([scriptblock]::Create($fn.Extent.Text))
        }
        function Out-Worker([string] $key, $decision) { "worker.$key=$($decision.Enabled)" }
        Out-Worker 'standard'   (Resolve-WorkerDeployment -EditionName 'Standard')
        Out-Worker 'enterprise' (Resolve-WorkerDeployment -EditionName 'Enterprise')
        Out-Worker 'developer'  (Resolve-WorkerDeployment -EditionName 'Developer')
        Out-Worker 'unknown'    (Resolve-WorkerDeployment -EditionName $null)
        Out-Worker 'express'    (Resolve-WorkerDeployment -EditionName 'Express')
        Out-Worker 'expressEnable' (Resolve-WorkerDeployment -EditionName 'Express' -EnableWorker)
        Out-Worker 'standardDisable' (Resolve-WorkerDeployment -EditionName 'Standard' -DisableWorker)
        try { Resolve-WorkerDeployment -EditionName 'Standard' -EnableWorker -DisableWorker | Out-Null; 'worker.both=accepted' }
        catch { 'worker.both=thrown' }

        function Out-Jobs([string] $key, $decision) {
            $mode = if ($decision.Set.Contains('ECR_Jobs__Queue__Mode')) { $decision.Set['ECR_Jobs__Queue__Mode'] } else { '-' }
            $executor = if ($decision.Set.Contains('ECR_Jobs__Recalculation__Executor')) { $decision.Set['ECR_Jobs__Recalculation__Executor'] } else { '-' }
            "jobs.$key.mode=$mode"
            "jobs.$key.executor=$executor"
            "jobs.$key.remove=$(@($decision.Remove) -join ',')"
            "jobs.$key.warnings=$(@($decision.Warnings).Count)"
        }
        Out-Jobs 'on'              (Resolve-JobExecutionConfig -WorkerEnabled $true)
        Out-Jobs 'onFileInProcess' (Resolve-JobExecutionConfig -WorkerEnabled $true -FileMode 'Quartz' -FileExecutor 'InProcess')
        Out-Jobs 'off'             (Resolve-JobExecutionConfig -WorkerEnabled $false)
        Out-Jobs 'offFileDatabase' (Resolve-JobExecutionConfig -WorkerEnabled $false -FileMode 'Database')
        Out-Jobs 'offFileWorker'   (Resolve-JobExecutionConfig -WorkerEnabled $false -FileExecutor 'Worker')

        $left = Remove-ServiceEnvironmentEntry -Existing @('ECR_Jobs__Queue__Mode=Database', 'ASPNETCORE_URLS=http://+:5000') -Name 'ECR_Jobs__Queue__Mode'
        "env.count=$($left.Count)"
        "env.first=$($left[0])"

        "file.mode=$(Get-ConfiguredValue -Path $Json -Keys 'Jobs', 'Queue', 'Mode')"
        "file.missing=$([string]::IsNullOrEmpty((Get-ConfiguredValue -Path $Json -Keys 'Jobs', 'Recalculation', 'Executor')))"

        "jsonc.placeholder=$(Test-ConfigIsPlaceholder -Path $Placeholder)"
        "jsonc.siteValues=$(Test-ConfigIsPlaceholder -Path $Commented)"
        "jsonc.plainValues=$(Test-ConfigIsPlaceholder -Path $Json)"
        "jsonc.edition=$(Get-ConfiguredEditionMode -Path $Commented)"
        "jsonc.value=$(Get-ConfiguredValue -Path $Commented -Keys 'Jobs', 'Queue', 'Mode')"

        "ps.major=$($PSVersionTable.PSVersion.Major)"
        try { Get-Content -Raw -LiteralPath $Commented | ConvertFrom-Json | Out-Null; 'jsonc.rawThrows=False' }
        catch { 'jsonc.rawThrows=True' }
        """;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Results = new(() => Run(FindPowerShell()));

    /// <summary>Той самий сценарій, але СТРОГО у вбудованому Windows PowerShell 5.1 (лише Windows).</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>?> WindowsPowerShellResults =
        new(() => WindowsPowerShell51() is { } exe ? Run(exe) : null);

    /// <remarks>
    /// Мутація (прогнано): у <c>Resolve-WorkerDeployment</c> гілку Express
    /// повернути <c>Enabled = $true</c> → <c>worker.express</c> червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("worker.standard", "True")]
    [InlineData("worker.enterprise", "True")]
    [InlineData("worker.developer", "True")]
    [InlineData("worker.unknown", "True")]
    [InlineData("worker.express", "False")]
    [InlineData("worker.expressEnable", "True")]
    [InlineData("worker.standardDisable", "False")]
    [InlineData("worker.both", "thrown")]
    public void Служба_воркера_типово_є_крім_Express_і_DisableWorker(string key, string expected)
        => Assert.Equal(expected, Value(key));

    /// <remarks>
    /// ⛔ Головний інваріант: Executor = Worker лише разом зі службою, без неї —
    /// InProcess навіть наперекір файлу майданчика. Мутація (прогнано): у гілці
    /// «служби немає» писати Executor лише зі значення файлу, а не InProcess →
    /// <c>jobs.off.executor</c>, <c>jobs.offFileDatabase.executor</c> і
    /// <c>jobs.offFileWorker.executor</c> червоні.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("jobs.on.mode", "Database")]
    [InlineData("jobs.on.executor", "Worker")]
    [InlineData("jobs.on.remove", "")]
    [InlineData("jobs.on.warnings", "0")]
    [InlineData("jobs.onFileInProcess.executor", "Worker")]
    [InlineData("jobs.onFileInProcess.warnings", "2")]
    [InlineData("jobs.off.mode", "Quartz")]
    [InlineData("jobs.off.executor", "InProcess")]
    [InlineData("jobs.off.remove", "")]
    [InlineData("jobs.offFileDatabase.mode", "-")]
    [InlineData("jobs.offFileDatabase.executor", "InProcess")]
    [InlineData("jobs.offFileDatabase.remove", "ECR_Jobs__Queue__Mode")]
    [InlineData("jobs.offFileWorker.executor", "InProcess")]
    [InlineData("jobs.offFileWorker.warnings", "1")]
    public void Режим_Api_пишеться_за_фактом_служби(string key, string expected)
        => Assert.Equal(expected, Value(key));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Прибирання_змінної_лишає_масив_і_чужі_записи()
    {
        // ⛔ Один залишений запис — рівно той випадок, де без `return ,(...)`
        // масив розгортається в рядок, і [0] дає символ, а не запис.
        Assert.Equal("1", Value("env.count"));
        Assert.Equal("ASPNETCORE_URLS=http://+:5000", Value("env.first"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Значення_з_файлу_майданчика_читається_за_вкладеним_ключем()
    {
        Assert.Equal("Database", Value("file.mode"));
        Assert.Equal("True", Value("file.missing"));
    }

    /// <remarks>
    /// ⛔ Порядок у тілі скрипта: перевірка «служба є після msiexec» стоїть ДО
    /// запису режиму Api, і режим береться з <c>$workerEnabled</c>, а не з
    /// сирого <c>-EnableWorker</c>. Інакше Executor = Worker міг би бути
    /// записаний за наміром, коли служби не встановилося.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Режим_Api_пишеться_після_перевірки_що_служба_встановилась()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var check = script.IndexOf("-and -not (Get-Service -Name EcrWorker", StringComparison.Ordinal);
        var write = script.IndexOf("Resolve-JobExecutionConfig -WorkerEnabled $workerEnabled", StringComparison.Ordinal);
        var msi = script.IndexOf("Start-Process msiexec", StringComparison.Ordinal);

        Assert.True(msi > 0 && check > msi, "перевірку наявності EcrWorker після msiexec не знайдено");
        Assert.True(write > check, "режим Api пишеться до перевірки служби або не за $workerEnabled");
        Assert.Contains("$workerFlag = if ($workerEnabled)", script, StringComparison.Ordinal);
    }

    /// <remarks>
    /// L10-05: заповнювач, який кладе MSI, містить коментарі <c>//</c>; Windows
    /// PowerShell 5.1 (так запускають runbook і install-guide) такий JSON не
    /// парсить. Предмет — справжній <c>src/Ecr.Api/appsettings.Production.json</c>.
    /// Мутація (прогнано в 5.1): <c>ConvertFrom-JsoncFile</c> замінити на
    /// <c>Get-Content -Raw | ConvertFrom-Json</c> → <c>jsonc.placeholder</c> = False,
    /// решта <c>jsonc.*</c> червоні. ⚠ У pwsh 7 коментарі дозволені, тож доказ — лише на 5.1.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("jsonc.placeholder", "True")]
    [InlineData("jsonc.siteValues", "False")]
    [InlineData("jsonc.plainValues", "False")]
    [InlineData("jsonc.edition", "Standard")]
    [InlineData("jsonc.value", "Database")]
    public void Файл_майданчика_з_коментарями_читається_як_у_Windows_PowerShell_5_1(string key, string expected)
        => Assert.Equal(expected, Value(key));

    /// <remarks>
    /// L10-05: основний прогін бере <c>pwsh</c> з PATH, а <c>pwsh</c> 7 ПРИЙМАЄ коментарі в JSON, тож
    /// сторож вище на ньому не червоніє, навіть якщо скрипт знову читатиме конфіг без <c>ConvertFrom-JsoncFile</c>.
    /// Тут сценарій іде через <c>%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe</c> (так запускають
    /// runbook і install-guide). Доказ середовища — <c>ps.major = 5</c> і <c>jsonc.rawThrows = True</c> (голий
    /// <c>ConvertFrom-Json</c> на файлі з коментарями тут падає); без нього зелене нічого б не означало.
    /// Мутація (CI, Windows; локально не запускалась): <c>ConvertFrom-JsoncFile</c> → <c>Get-Content -Raw | ConvertFrom-Json</c>
    /// → <c>jsonc.*</c> червоні. ⚠ На не-Windows 5.1 немає: там перевіряється лише це, а справжній прогін
    /// іде у джобі <c>worker (windows)</c> (ci.yml, крок «Windows PowerShell 5.1»).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Файл_майданчика_з_коментарями_читається_саме_у_Windows_PowerShell_5_1_з_каталогу_системи()
    {
        var results = WindowsPowerShellResults.Value;
        if (results is null)
        {
            // Не Windows: вбудованого 5.1 немає; Windows без нього — збій середовища, а не пропуск (див. WindowsPowerShell51).
            Assert.False(OperatingSystem.IsWindows(), "на Windows немає powershell.exe 5.1");
            return;
        }

        Assert.Equal("5", ValueOf(results, "ps.major"));
        Assert.Equal("True", ValueOf(results, "jsonc.rawThrows"));
        Assert.Equal("True", ValueOf(results, "jsonc.placeholder"));
        Assert.Equal("False", ValueOf(results, "jsonc.siteValues"));
        Assert.Equal("False", ValueOf(results, "jsonc.plainValues"));
        Assert.Equal("Standard", ValueOf(results, "jsonc.edition"));
        Assert.Equal("Database", ValueOf(results, "jsonc.value"));
    }

    private static string ValueOf(IReadOnlyDictionary<string, string> results, string key)
    {
        Assert.True(results.TryGetValue(key, out var value), $"ключа {key} немає у виводі:\n{string.Join('\n', results)}");
        return value!;
    }

    /// <summary>Вбудований Windows PowerShell 5.1: <c>%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe</c>; не Windows — <c>null</c>.</summary>
    private static string? WindowsPowerShell51()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

        // ⛔ Не пропуск: Windows без вбудованого 5.1 — збій середовища, сторож не має мовчати.
        return File.Exists(exe) ? exe : throw new InvalidOperationException($"Немає {exe}: тест не може виконати deploy-ecr.ps1 у Windows PowerShell 5.1.");
    }

    private static string Value(string key)
    {
        Assert.True(Results.Value.TryGetValue(key, out var value), $"ключа {key} немає у виводі:\n{string.Join('\n', Results.Value)}");
        return value!;
    }

    private static Dictionary<string, string> Run(string powershell)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ecr-deploy-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var harness = Path.Combine(dir, "harness.ps1");
            File.WriteAllText(harness, Harness);
            var json = Path.Combine(dir, "site.json");
            File.WriteAllText(json, """{ "Jobs": { "Queue": { "Mode": "Database" } } }""");

            var start = new ProcessStartInfo(powershell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var commented = Path.Combine(dir, "commented.json");
            File.WriteAllText(commented, """
                {
                  // коментар майданчика
                  "Database": { "EditionMode": "Standard" }, /* блоковий */
                  "Jobs": { "Queue": { "Mode": "Database" } }
                }
                """);

            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", harness,
                         "-Deploy", Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"), "-Json", json,
                         "-Placeholder", Path.Combine(SourceTree.Root, "src", "Ecr.Api", "appsettings.Production.json"),
                         "-Commented", commented })
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

    /// <summary><c>pwsh</c> з PATH; на Windows без нього — вбудований <c>powershell.exe</c>.</summary>
    private static string FindPowerShell()
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
