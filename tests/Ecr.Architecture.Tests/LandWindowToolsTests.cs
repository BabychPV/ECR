// tests/Ecr.Architecture.Tests/LandWindowToolsTests.cs
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Інструменти вікна паритету в репозиторії (раніше лежали поза ним з жорсткими шляхами/портом/базою репетицій):
/// правило категорії ECW_C12_03 у <c>af-land-category-rules.json</c>, склад речовин з довідника застосовності,
/// <c>Load-LandInputCells.ps1</c>, <c>Invoke-LandRecalc.ps1</c>, <c>New-LandMethodologyDraft.ps1</c>.
/// Мережі й БД немає: перевіряються правило, чисті функції на синтетичних даних, відмови охорони стенда і текст
/// скриптів (жодних шляхів F:\ / H:\, портів репетицій, імен баз і документів прода).
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): вилучити правило ECW_C12_03 з файлу правил - червоніє
/// <see cref="Правило_C12_03_є_у_файлі_без_обгортки_і_дає_ключ_групи_0026_для_шести_NAE"/>; прибрати
/// <c>Initialize-LandStandSession</c> з Load-LandInputCells.ps1 - червоніє
/// <see cref="Запис_без_SqlConnection_ConfirmStand_і_на_чужому_стенді_відмовляє_до_читання_файлів"/>.
/// </remarks>
public sealed class LandWindowToolsTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static readonly string[] Tools =
    [
        "Load-LandInputCells.ps1", "Invoke-LandRecalc.ps1", "New-LandMethodologyDraft.ps1",
    ];

    private const string Dm = "{\"DOC-7\":14}";

    private static readonly string[] Group0026 = ["0026", "0030", "0036", "0038", "0040", "0042"];

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
        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-window-{Guid.NewGuid():N}.ps1");
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

    /// <summary>Запускає інструмент у дочірньому PowerShell; виняток скрипта - код 7 і текст у виході.</summary>
    private static (int Exit, string Output) RunTool(string tool, params string[] toolArguments)
    {
        // Імена -Param і значення збираються в хеш-таблицю: розгортання масиву (`@rest`) іменованих параметрів не зв'язує.
        const string wrapper = """
            $ErrorActionPreference = 'Stop'
            $toolPath = $args[0]
            $bound = @{}
            $i = 1
            while ($i -lt $args.Count) {
              $name = $args[$i].TrimStart('-')
              if ($i + 1 -lt $args.Count -and -not $args[$i + 1].StartsWith('-')) { $bound[$name] = $args[$i + 1]; $i += 2 }
              else { $bound[$name] = [switch]::Present; $i += 1 }
            }
            $global:LASTEXITCODE = 0
            try { & $toolPath @bound; exit $global:LASTEXITCODE } catch { Write-Output $_.Exception.Message; exit 7 }
            """;
        return RunPowerShell(wrapper, [LandFile(tool), .. toolArguments]);
    }

    private static string RulesText() => File.ReadAllText(Path.Combine(Dir, "af-land-category-rules.json"));

    private static string C1203Rule()
    {
        using var doc = JsonDocument.Parse(RulesText());
        var rule = doc.RootElement.GetProperty("rules").EnumerateArray()
            .Single(r => r.GetProperty("methodology").GetString() == "ECW_C12_03");
        return rule.GetProperty("expression").GetString()!;
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_C12_03_є_у_файлі_без_обгортки_і_дає_ключ_групи_0026_для_шести_NAE()
    {
        var rule = C1203Rule();

        // Обгортку SUBSTANCE_APPLICABLE ставить -Stage SubstanceApplicability; у файлі - базове правило.
        Assert.DoesNotContain("SUBSTANCE_APPLICABLE", rule, StringComparison.Ordinal);
        Assert.Contains("@IDCodeNAEProject", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("@Land_NAE", rule, StringComparison.Ordinal);
        Assert.True(Expr.Parse(rule, ExpressionDialect.Methodology).IsSuccess, rule);

        string Key(string nae)
        {
            var context = new TestEvaluationContext();
            context.Arguments["IDCodeNAEProject"] = ExpressionValue.Text(nae);
            var value = Expr.Eval(rule, context, ExpressionDialect.Methodology);
            Assert.False(value.IsError, $"{nae}: {value.ErrorCode}");
            return (string)value.Value!;
        }

        Assert.All(Group0026, nae => Assert.Equal("0026", Key(nae)));
        Assert.Equal(string.Empty, Key("0025"));
        Assert.Equal(string.Empty, Key("0009"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_C12_03_обгортається_шаблоном_моделі_і_розбирається()
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "land-template.model.json")));
        var description = model.RootElement.GetProperty("substanceApplicability");
        var entry = description.GetProperty("methodologies").GetProperty("ECW_C12_03");
        Assert.Equal("TypeFuel", entry.GetProperty("kind").GetString());

        var wrapped = description.GetProperty("ruleTemplate").GetString()!
            .Replace("{methodology}", "ECW_C12_03", StringComparison.Ordinal)
            .Replace("{kind}", entry.GetProperty("kind").GetString()!, StringComparison.Ordinal)
            .Replace("{valueExpression}", entry.GetProperty("valueExpression").GetString()!, StringComparison.Ordinal)
            .Replace("{rule}", C1203Rule(), StringComparison.Ordinal);

        Assert.True(Expr.Parse(wrapped, ExpressionDialect.Methodology).IsSuccess, wrapped);
        Assert.Contains("@IDCodeNAEProject", wrapped, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_LandParity_ставить_C12_03_сам_склад_із_словника_а_Strict_дає_CompositionMass()
    {
        // Правило потрапляє на чернетку тим самим етапом, що й усі правила файлу, а склад речовин для позначеного
        // правила береться з довідника застосовності (15 кодів), не з суфіксів констант.
        using var rules = JsonDocument.Parse(RulesText());
        var rule = rules.RootElement.GetProperty("rules").EnumerateArray()
            .Single(r => r.GetProperty("methodology").GetString() == "ECW_C12_03");
        Assert.True(rule.GetProperty("substancesFromApplicability").GetBoolean());

        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));
        var parity = build.IndexOf("function Stage-LandParity", StringComparison.Ordinal);
        var next = build.IndexOf("function Stage-MassFraction", parity, StringComparison.Ordinal);
        Assert.True(parity > 0 && next > parity);
        var stage = build[parity..next];
        Assert.Contains("substancesFromApplicability", stage, StringComparison.Ordinal);
        Assert.Contains("Resolve-LandMethodologySubstanceCodes", stage, StringComparison.Ordinal);

        // NumericMode=Strict - у етапі CompositionMass; C12_03 є в його моделі (фрагмент складу).
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "land-template.model.json")));
        var composition = model.RootElement.GetProperty("argumentBridges").GetProperty("hse400")
            .GetProperty("compositionMass").GetProperty("methodologies");
        Assert.True(composition.TryGetProperty("ECW_C12_03", out var c1203));
        Assert.Contains("CURRENT_SUBSTANCE()", c1203.GetProperty("fragment").GetString(), StringComparison.Ordinal);
        var composeStart = build.IndexOf("function Stage-CompositionMass", StringComparison.Ordinal);
        var composeEnd = build.IndexOf("function Stage-SubstanceApplicability", composeStart, StringComparison.Ordinal);
        Assert.Contains("numericMode = 'Strict'", build[composeStart..composeEnd], StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Склад_речовин_C12_03_це_п_ятнадцять_кодів_словника_і_правило_дає_аргумент_тест_кейсу()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            . $args[1]
            $sa = Get-Content -LiteralPath $args[2] -Raw -Encoding UTF8 | ConvertFrom-Json
            $codes = Get-LandApplicabilityCodeUnion -Codes $sa.methodologies.ECW_C12_03.codes
            "COUNT=$($codes.Count)"
            "CODES=" + ($codes -join ',')
            $empty = Get-LandApplicabilityCodeUnion -Codes $null
            "EMPTY=$($empty.Count)"
            $rules = (Get-Content -LiteralPath $args[3] -Raw -Encoding UTF8 | ConvertFrom-Json).rules
            $r = $rules | Where-Object { $_.methodology -eq 'ECW_C12_03' }
            $s = Get-LandRuleSampleArguments -Expression ([string]$r.expression) -NaeCandidate '6646'
            "SAMPLE=" + (($s.Keys | Sort-Object | ForEach-Object { "$_=$($s[$_])" }) -join ';')
            """;

        var (exit, output) = RunPowerShell(
            script,
            LandFile("LandSubstanceApplicability.Lib.ps1"),
            LandFile("LandParityRuleArgs.Lib.ps1"),
            Path.Combine(Dir, "land-substance-applicability.json"),
            Path.Combine(Dir, "af-land-category-rules.json"));

        Assert.True(exit == 0, output);
        Assert.Contains("COUNT=15", output, StringComparison.Ordinal);
        Assert.Contains("CODES=333,334,370,415,416,602,616,621,627,1702,1707,1715,1720,1728,2754", output, StringComparison.Ordinal);
        Assert.Contains("EMPTY=0", output, StringComparison.Ordinal);
        Assert.Contains("SAMPLE=IDCodeNAEProject=0026", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етапи_Build_приймають_OnlyMethodology_і_за_замовчуванням_обробляють_усі()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));

        Assert.Contains("[string[]]$OnlyMethodology = @()", build, StringComparison.Ordinal);
        Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Count(build, @"\$OnlyMethodology\.Count -gt 0 -and \$OnlyMethodology -notcontains \$name"));
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("Load-LandInputCells.ps1")]
    [InlineData("Invoke-LandRecalc.ps1")]
    [InlineData("New-LandMethodologyDraft.ps1")]
    [InlineData("LandInputCells.Lib.ps1")]
    [InlineData("LandStandSession.Lib.ps1")]
    public void Інструменти_вікна_не_містять_шляхів_портів_баз_документів_і_секретів_репетицій(string file)
    {
        var text = File.ReadAllText(LandFile(file));

        string[] forbidden =
        [
            @"F:\", @"H:\", @"C:\", @"D:\", "5101", "5121", "5099", "AccP", "acc-rcp", "integ-land",
            "ecr-wt", "LAND-PROD", "accounts.local", "-AsPlainText", "prod-sample",
        ];
        foreach (var token in forbidden)
        {
            Assert.DoesNotContain(token, text, StringComparison.OrdinalIgnoreCase);
        }

        // Приклад формату підтвердження `localhost:5094/EcrLandUi` дозволений лише в довідці (.EXAMPLE/.PARAMETER).
        var code = StripHelp(text);
        Assert.DoesNotContain("5094", code, StringComparison.Ordinal);
        Assert.DoesNotContain("5092", code, StringComparison.Ordinal);
        Assert.DoesNotContain("EcrLandUi", code, StringComparison.Ordinal);
        Assert.DoesNotContain("$env:ECR_BASE_URL", code, StringComparison.Ordinal);

        // Id документів не зашиваються: вони приходять лише в -DocMap.
        Assert.DoesNotMatch(@"foreach\s*\(\s*\$\w+\s+in\s+\d+\s*,\s*\d+", code);
        Assert.DoesNotMatch(@"\$\w*[dD]oc\w*Id\s*=\s*\d", code);
    }

    private static string StripHelp(string text)
    {
        var start = text.IndexOf("<#", StringComparison.Ordinal);
        var end = text.IndexOf("#>", StringComparison.Ordinal);
        return start >= 0 && end > start ? text.Remove(start, end + 2 - start) : text;
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("Load-LandInputCells.ps1")]
    [InlineData("Invoke-LandRecalc.ps1")]
    [InlineData("New-LandMethodologyDraft.ps1")]
    public void Інструменти_вікна_мають_обовязкові_BaseUrl_ConfirmStand_без_дефолтів_і_охорону_до_входу(string file)
    {
        var text = File.ReadAllText(LandFile(file));

        Assert.Contains("[Parameter(Mandatory)][string]$BaseUrl,", text, StringComparison.Ordinal);
        Assert.Contains("[Parameter(Mandatory)][string]$ConfirmStand,", text, StringComparison.Ordinal);
        Assert.Contains("[string]$SqlConnection,", text, StringComparison.Ordinal);
        Assert.Contains("[switch]$Execute", text, StringComparison.Ordinal);
        Assert.Contains("[switch]$DryRun", text, StringComparison.Ordinal);
        Assert.Contains("$exec = ($Execute.IsPresent -and -not $DryRun.IsPresent)", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\[string\]\$(SqlConnection|ConfirmStand|BaseUrl)\s*=", text);

        var guard = text.IndexOf("Initialize-LandStandSession", StringComparison.Ordinal);
        var login = text.IndexOf("Connect-Ecr -BaseUrl", StringComparison.Ordinal);
        Assert.True(guard > 0, $"{file}: немає охорони стенда.");
        Assert.True(login > guard, $"{file}: охорона має стояти до входу.");

        // Усі мутації - лише через Invoke-Ecr/Invoke-LandEcrRetry (guard ConfirmStand в Invoke-Ecr).
        Assert.DoesNotContain("Invoke-WebRequest", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-RestMethod", text, StringComparison.Ordinal);
        // EcrApi.ps1 підключається через каталог скрипта, не абсолютним шляхом.
        Assert.Contains(". (Join-Path $here 'EcrApi.ps1')", text, StringComparison.Ordinal);
        Assert.Contains("$PSScriptRoot", text, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("Load-LandInputCells.ps1")]
    [InlineData("Invoke-LandRecalc.ps1")]
    public void Завантажувач_і_перерахунок_беруть_документи_лише_з_DocMap(string file)
    {
        var text = File.ReadAllText(LandFile(file));

        Assert.Contains("[Parameter(Mandatory)][string]$DocMap,", text, StringComparison.Ordinal);
        Assert.Contains("ConvertFrom-LandDocMap -Json $DocMap", text, StringComparison.Ordinal);
        // Пошуку документа за назвою на стенді немає.
        Assert.DoesNotContain("nameL10n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("documents?limit", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Охорона_стенда_вимагає_SqlConnection_ConfirmStand_і_локальний_стенд_для_запису()
    {
        var lib = File.ReadAllText(LandFile("LandStandSession.Lib.ps1"));
        var at = lib.IndexOf("function Initialize-LandStandSession", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var body = lib[at..lib.IndexOf("function Get-LandRetryAfterSeconds", at, StringComparison.Ordinal)];

        var local = body.IndexOf("Assert-LocalBaseUrl $BaseUrl", StringComparison.Ordinal);
        var named = body.IndexOf("Assert-LandConfirmStandNamesStand", StringComparison.Ordinal);
        var sql = body.IndexOf("Assert-LocalSqlConnection $SqlConnection", StringComparison.Ordinal);
        var confirm = body.IndexOf("Set-EcrStandConfirmation -Stand $ConfirmStand -Url $BaseUrl -Database $standDb", StringComparison.Ordinal);
        Assert.True(local >= 0 && named > local && sql > named && confirm > sql,
            "Порядок: локальний API, ConfirmStand називає стенд, локальний SQL (EcrLand*), підтвердження з базою.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Запис_без_SqlConnection_ConfirmStand_і_на_чужому_стенді_відмовляє_до_читання_файлів()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), $"ecr-land-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            string[] common = ["-Kind", "TypeFuel", "-SourcePath", emptyDir, "-DocMap", Dm];

            // Без -SqlConnection.
            var (e1, o1) = RunTool("Load-LandInputCells.ps1", [.. common, "-BaseUrl", "http://localhost:5092", "-ConfirmStand", "localhost:5092", "-Execute"]);
            Assert.Equal(7, e1);
            Assert.Contains("-SqlConnection", o1, StringComparison.Ordinal);

            // ConfirmStand не називає цей стенд - відмова навіть без -Execute.
            var (e2, o2) = RunTool("Load-LandInputCells.ps1", [.. common, "-BaseUrl", "http://localhost:5092", "-ConfirmStand", "other-host:1"]);
            Assert.Equal(7, e2);
            Assert.Contains("-ConfirmStand", o2, StringComparison.Ordinal);

            // Підтвердження лише адреси, без імені бази, - відмова запису з назвою бази.
            var (e3, o3) = RunTool(
                "Load-LandInputCells.ps1",
                [.. common, "-BaseUrl", "http://localhost:5092", "-ConfirmStand", "localhost:5092", "-Execute",
                 "-SqlConnection", "Server=localhost;Database=EcrLandWindowTest;Integrated Security=true"]);
            Assert.Equal(7, e3);
            Assert.Contains("EcrLandWindowTest", o3, StringComparison.Ordinal);

            // Локальний прод-інстанс (база не EcrLand*).
            var (e4, o4) = RunTool(
                "Invoke-LandRecalc.ps1",
                "-DocMap", Dm, "-PeriodKey", "202601", "-BaseUrl", "http://localhost:5092", "-Execute",
                "-SqlConnection", "Server=localhost;Database=ECR;Integrated Security=true", "-ConfirmStand", "localhost:5092/ECR");
            Assert.Equal(7, e4);
            Assert.Contains("EcrLand", o4, StringComparison.Ordinal);

            // Чужий API - відмова навіть без -Execute.
            foreach (var tool in new[] { "Load-LandInputCells.ps1", "Invoke-LandRecalc.ps1", "New-LandMethodologyDraft.ps1" })
            {
                var args = tool switch
                {
                    "Load-LandInputCells.ps1" => common,
                    "Invoke-LandRecalc.ps1" => new[] { "-DocMap", Dm, "-PeriodKey", "202601" },
                    _ => new[] { "-Methodology", "ECW_C12_03", "-VersionNumber", "V2" },
                };
                var (exit, output) = RunTool(tool, [.. args, "-BaseUrl", "https://ecr.example.com", "-ConfirmStand", "ecr.example.com"]);
                Assert.Equal(7, exit);
                Assert.Contains("localhost", output, StringComparison.Ordinal);
            }

            // Без -ConfirmStand/-BaseUrl/-DocMap скрипт не стартує (обов'язкові параметри, дефолтів немає).
            var (missingExit, _) = RunTool("Load-LandInputCells.ps1", "-Kind", "TypeFuel", "-SourcePath", emptyDir, "-DocMap", Dm, "-BaseUrl", "http://localhost:5092");
            Assert.NotEqual(0, missingExit);
            var (noMapExit, _) = RunTool("Invoke-LandRecalc.ps1", "-PeriodKey", "202601", "-BaseUrl", "http://localhost:5092", "-ConfirmStand", "localhost:5092");
            Assert.NotEqual(0, noMapExit);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    private const string SampleJson = """
        {"periods":{"2026-01":{
          "AF_Air_Land_Off_Utility_DI_10_01":{"rows":[
            {"Land_TypeFuel":"MARKER-VALUE-XYZ - fuel","Land_Measure_ReleaseColdVent":"Yes"},
            {"Land_TypeFuel":"NULL"},
            {"Land_TypeFuel":"LPG"}]},
          "AF_Air_Land_Off_Utility_DI_10_03":{"rows":[
            {"Land_TypeFuel":"A","Land_Measure_GTU_Month":"01","Land_Measure_GTU1_301":"1.5","Land_Measure_GTU5_301":"2"}]},
          "Other_Table":{"rows":[{"Land_TypeFuel":"A"}]}}}}
        """;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_без_облікового_запису_лише_читає_файли_і_друкує_лічильники_без_значень()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecr-land-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "DOC-7.json"), SampleJson);
            string[] common = ["-SourcePath", dir, "-DocMap", Dm, "-BaseUrl", "http://localhost:5092", "-ConfirmStand", "localhost:5092"];

            var (exit, output) = RunTool("Load-LandInputCells.ps1", [.. common, "-Kind", "TypeFuel"]);

            Assert.True(exit == 0, output);
            Assert.Contains("PLAN (TypeFuel): table-periods 2, rows 3, cells 4 (TypeFuel 3, ColdVent 1, Gtu 0)", output, StringComparison.Ordinal);
            Assert.Contains("DOC-7|202601|T10_01", output, StringComparison.Ordinal);
            Assert.DoesNotContain("MARKER-VALUE-XYZ", output, StringComparison.Ordinal);

            var (gtuExit, gtuOutput) = RunTool("Load-LandInputCells.ps1", [.. common, "-Kind", "Gtu", "-PeriodKey", "202601", "-DryRun", "-Execute"]);
            Assert.True(gtuExit == 0, gtuOutput);
            Assert.Contains("PLAN (Gtu): table-periods 1, rows 1, cells 2", gtuOutput, StringComparison.Ordinal);

            // Запис без облікового запису - відмова, але не вихід 0.
            var (noUserExit, _) = RunTool(
                "Load-LandInputCells.ps1",
                [.. common, "-Kind", "Gtu", "-Execute", "-SqlConnection", "Server=localhost;Database=EcrLandWindowTest;Integrated Security=true",
                 "-ConfirmStand", "localhost:5092/EcrLandWindowTest"]);
            Assert.NotEqual(0, noUserExit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Фальшивий API на loopback: документ 14 - NO_DOC (404), документ 15 має таблицю T10_03 лише з двома колонками.
    /// Фіксує всі запити, щоб довести: PATCH не надсилається, поки є NO_DOC / відсутня колонка.
    /// </summary>
    private sealed class FakeStand : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Thread _thread;
        public List<string> Requests { get; } = [];
        public int Port { get; }

        public FakeStand()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _thread = new Thread(Loop) { IsBackground = true };
            _thread.Start();
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    return;
                }

                var path = ctx.Request.Url!.PathAndQuery;
                lock (Requests)
                {
                    Requests.Add($"{ctx.Request.HttpMethod} {path}");
                }

                var status = 200;
                var body = "{}";
                if (path.StartsWith("/api/v1/registries", StringComparison.Ordinal))
                {
                    body = "[]";
                }
                else if (path.StartsWith("/api/v1/documents/14", StringComparison.Ordinal))
                {
                    status = 404;
                    body = "{\"errorCode\":\"ECR-DOC-0404\"}";
                }
                else if (path is "/api/v1/documents/15" or "/api/v1/documents/16" or "/api/v1/documents/17")
                {
                    // 15 - DOC-7, 16 - чужа назва, 17 - DOC-8.
                    var name = path.EndsWith("15", StringComparison.Ordinal) ? "DOC-7" : path.EndsWith("16", StringComparison.Ordinal) ? "OTHER" : "DOC-8";
                    body = "{\"id\":" + path[^2..] + ",\"nameL10n\":{\"values\":{\"en\":\"" + name + "\"}}}";
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(path, "^/api/v1/documents/1[567]/tables/5"))
                {
                    body = "{\"columns\":[{\"code\":\"Land_Measure_GTU_Month\"},{\"code\":\"Land_Measure_GTU2_301\"}],"
                        + "\"rows\":[{\"rowKey\":\"p7-0001\",\"rowVersion\":\"1\"},{\"rowKey\":\"p8-0001\",\"rowVersion\":\"1\"}]}";
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(path, "^/api/v1/documents/1[567]/tables"))
                {
                    body = "[{\"sheetCode\":\"S10_OFF_UTILITY_DI\",\"tableCode\":\"T10_03\",\"tableInstanceId\":5}]";
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Close();
            _thread.Join(2000);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void NO_DOC_NO_TABLE_і_відсутня_колонка_дають_ненульовий_код_і_запис_не_починається()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ecr-land-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        using var stand = new FakeStand();
        try
        {
            File.WriteAllText(Path.Combine(dir, "DOC-7.json"), SampleJson);
            var url = $"http://localhost:{stand.Port}";
            string[] common =
            [
                "-Kind", "Gtu", "-SourcePath", dir, "-BaseUrl", url, "-ConfirmStand", $"localhost:{stand.Port}",
                "-User", "tester", "-PeriodKey", "202601",
            ];

            // Документ 14 на стенді немає (404): NO_DOC, код 1.
            var env = new Dictionary<string, string> { ["ECR_PASSWORD"] = "x" };
            var (noDocExit, noDoc) = RunToolWithEnv(env, "Load-LandInputCells.ps1", [.. common, "-DocMap", Dm]);
            Assert.Equal(1, noDocExit);
            Assert.Contains("NO_DOC 1", noDoc, StringComparison.Ordinal);

            // Документ 15: колонок GTU1_301 немає (лише Month і GTU2_301): пропущено через відсутню колонку, код 1.
            var (missingExit, missing) = RunToolWithEnv(env, "Load-LandInputCells.ps1", [.. common, "-DocMap", "{\"DOC-7\":15}"]);
            Assert.Equal(1, missingExit);
            Assert.Contains("skipped-missing-column cells 1", missing, StringComparison.Ordinal);
            Assert.Contains("T10_03/Land_Measure_GTU1_301", missing, StringComparison.Ordinal);
            // GTU5 на T10_03: у моделі колонки немає - не "відсутня на стенді", а окремий лічильник із WARN.
            Assert.Contains("WARN source-without-target-column", missing, StringComparison.Ordinal);
            Assert.Contains("T10_03/Land_Measure_GTU5_301=1", missing, StringComparison.Ordinal);
            Assert.DoesNotContain("MISSING COLUMNS (table/column, no values; compare with the model bridges): T10_03/Land_Measure_GTU1_301, T10_03/Land_Measure_GTU5_301", missing, StringComparison.Ordinal);

            // З явним -AllowMissingColumns план проходить (відсутні колонки лишаються у звіті).
            var (okExit, ok) = RunToolWithEnv(env, "Load-LandInputCells.ps1", [.. common, "-DocMap", "{\"DOC-7\":15}", "-AllowMissingColumns"]);
            Assert.Equal(0, okExit);
            Assert.Contains("PLAN (no writes)", ok, StringComparison.Ordinal);

            // Лише GTU5 без цільової колонки (решта колонок на стенді є): WARN, код 0.
            File.WriteAllText(
                Path.Combine(dir, "DOC-8.json"),
                "{\"periods\":{\"2026-01\":{\"AF_Air_Land_Off_Utility_DI_10_03\":{\"rows\":[{\"Land_Measure_GTU_Month\":\"01\",\"Land_Measure_GTU2_301\":\"1\",\"Land_Measure_GTU5_301\":\"2\"}]}}}}");
            var (orphanExit, orphan) = RunToolWithEnv(env, "Load-LandInputCells.ps1", [.. common, "-DocMap", "{\"DOC-8\":17}"]);
            Assert.Equal(0, orphanExit);
            Assert.Contains("WARN source-without-target-column", orphan, StringComparison.Ordinal);
            Assert.Contains("skipped-missing-column cells 0", orphan, StringComparison.Ordinal);

            // Ім'я документа на стенді інше, ніж ключ мапи: NAME_MISMATCH, код 1; -AllowNameMismatch обходить.
            var (nameExit, name) = RunToolWithEnv(env, "Load-LandInputCells.ps1", [.. common, "-DocMap", "{\"DOC-7\":16}"]);
            Assert.Equal(1, nameExit);
            Assert.Contains("NAME_MISMATCH 1", name, StringComparison.Ordinal);
            var (allowExit, _) = RunToolWithEnv(
                env, "Load-LandInputCells.ps1", [.. common, "-DocMap", "{\"DOC-7\":16}", "-AllowNameMismatch", "-AllowMissingColumns"]);
            Assert.Equal(0, allowExit);

            // Режим запису з NO_DOC зупиняється до першого PATCH.
            var (execExit, exec) = RunToolWithEnv(
                env, "Load-LandInputCells.ps1",
                [.. common, "-DocMap", Dm, "-Execute", "-SqlConnection", "Server=localhost;Database=EcrLandWindowTest;Integrated Security=true",
                 "-ConfirmStand", $"localhost:{stand.Port}/EcrLandWindowTest"]);
            Assert.Equal(1, execExit);
            Assert.Contains("WRITE STOPPED", exec, StringComparison.Ordinal);
            lock (stand.Requests)
            {
                Assert.DoesNotContain(stand.Requests, r => r.StartsWith("PATCH ", StringComparison.Ordinal));
            }

            // Перерахунок: NO_DOC у плані - код 1, жодного POST /recalculate.
            var (recalcExit, recalc) = RunToolWithEnv(
                env, "Invoke-LandRecalc.ps1",
                ["-DocMap", Dm, "-PeriodKey", "202601", "-BaseUrl", url, "-ConfirmStand", $"localhost:{stand.Port}", "-User", "tester"]);
            Assert.Equal(1, recalcExit);
            Assert.Contains("NO_DOC", recalc, StringComparison.Ordinal);

            var (recalcNameExit, recalcName) = RunToolWithEnv(
                env, "Invoke-LandRecalc.ps1",
                ["-DocMap", "{\"DOC-7\":16}", "-PeriodKey", "202601", "-BaseUrl", url, "-ConfirmStand", $"localhost:{stand.Port}", "-User", "tester"]);
            Assert.Equal(1, recalcNameExit);
            Assert.Contains("NAME_MISMATCH", recalcName, StringComparison.Ordinal);
            lock (stand.Requests)
            {
                Assert.DoesNotContain(stand.Requests, r => r.Contains("/recalculate", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static (int Exit, string Output) RunToolWithEnv(Dictionary<string, string> env, string tool, params string[] toolArguments)
    {
        foreach (var (key, value) in env)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            return RunTool(tool, toolArguments);
        }
        finally
        {
            foreach (var key in env.Keys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Чисті_функції_зіставляють_довідник_число_ключ_рядка_і_DocMap()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            . $args[1]
            "PREFIX=" + (Get-LandRowKeyPrefix -DocumentName 'DOC-7')
            try { Get-LandRowKeyPrefix -DocumentName 'NODIGITS' | Out-Null; 'PREFIX-NOREFUSE' } catch { 'PREFIX-REFUSED' }
            $lookup = New-LandLookupMap -Entries @(
              [pscustomobject]@{ id = 11; code = 'FUEL_HMB'; display = 'Fuel gas (HMB)' },
              [pscustomobject]@{ id = 12; code = 'Yes'; display = 'Yes' })
            "HEAD=" + (Resolve-LandLookupId -Map $lookup -Text 'Fuel gas (HMB) - Топливный газ (МТБ)')
            "CODE=" + (Resolve-LandLookupId -Map $lookup -Text 'fuel_hmb')
            "YES=" + (Resolve-LandLookupId -Map $lookup -Text ' YES ')
            $miss = Resolve-LandLookupId -Map $lookup -Text 'Unknown fuel'
            "MISS=" + $(if ($null -eq $miss) { 'null' } else { $miss })
            "NUM=" + (ConvertTo-LandInputNumber -Text '1.25')
            $bad = ConvertTo-LandInputNumber -Text 'abc'
            "BAD=" + $(if ($null -eq $bad) { 'null' } else { $bad })
            $nan = ConvertTo-LandInputNumber -Text 'NaN'
            "NAN=" + $(if ($null -eq $nan) { 'null' } else { $nan })
            $gtu = Get-LandInputCellKind -Name 'Gtu'
            "GTUCOLS=$($gtu.Columns.Count)"
            $docs = ConvertFrom-LandDocMap -Json '{"A-10":14,"A-11":15}'
            "DOCMAP=" + (($docs.Keys | ForEach-Object { "$_=$($docs[$_])" }) -join ';')
            foreach ($bad in '[]', '{}', 'not json', '{"A":0}', '{"A":"x"}') {
              try { ConvertFrom-LandDocMap -Json $bad | Out-Null; "DOCMAP-ACCEPTED:$bad" } catch { 'DOCMAP-REFUSED' }
            }
            foreach ($okStand in 'localhost:5094', 'http://localhost:5094', 'localhost', 'localhost:5094/EcrLandUi', 'http://localhost:5094/EcrLandUi') {
              try { Assert-LandConfirmStandNamesStand -BaseUrl 'http://localhost:5094' -ConfirmStand $okStand; 'STAND-OK' } catch { "STAND-WRONGLY-REFUSED:$okStand" }
            }
            foreach ($badStand in '', 'other-host:1', 'localhost:5095/Db') {
              try { Assert-LandConfirmStandNamesStand -BaseUrl 'http://localhost:5094' -ConfirmStand $badStand; "STAND-ACCEPTED:$badStand" } catch { 'STAND-REFUSED' }
            }
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandInputCells.Lib.ps1"), LandFile("LandStandSession.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("PREFIX=p7", output, StringComparison.Ordinal);
        Assert.Contains("PREFIX-REFUSED", output, StringComparison.Ordinal);
        Assert.Contains("HEAD=11", output, StringComparison.Ordinal);
        Assert.Contains("CODE=11", output, StringComparison.Ordinal);
        Assert.Contains("YES=12", output, StringComparison.Ordinal);
        Assert.Contains("MISS=null", output, StringComparison.Ordinal);
        Assert.Contains("NUM=1.25", output, StringComparison.Ordinal);
        Assert.Contains("BAD=null", output, StringComparison.Ordinal);
        Assert.Contains("NAN=null", output, StringComparison.Ordinal);
        Assert.Contains("GTUCOLS=26", output, StringComparison.Ordinal);
        Assert.Contains("DOCMAP=A-10=14;A-11=15", output, StringComparison.Ordinal);
        Assert.Equal(5, output.Split("DOCMAP-REFUSED").Length - 1);
        Assert.DoesNotContain("DOCMAP-ACCEPTED", output, StringComparison.Ordinal);
        Assert.Equal(5, output.Split("STAND-OK").Length - 1);
        Assert.Equal(3, output.Split("STAND-REFUSED").Length - 1);
        Assert.DoesNotContain("STAND-WRONGLY", output, StringComparison.Ordinal);
        Assert.DoesNotContain("STAND-ACCEPTED", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void GTU_колонки_по_таблично_з_моделі_T10_03_без_GTU5_T11_03_з_GTU5()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $model = Get-Content -LiteralPath $args[1] -Raw -Encoding UTF8 | ConvertFrom-Json
            $cols = Get-LandModelTableColumns -Model $model -TableCodes @('T10_03', 'T11_03')
            "T10_03=$($cols['T10_03'].Count) GTU5=$(@($cols['T10_03'] | Where-Object { $_ -like 'Land_Measure_GTU5_*' }).Count)"
            "T11_03=$($cols['T11_03'].Count) GTU5=$(@($cols['T11_03'] | Where-Object { $_ -like 'Land_Measure_GTU5_*' }).Count)"
            $doc = @{ periods = @{ '2026-01' = @{
                'AF_Air_Land_Off_Utility_DI_10_03' = @{ rows = @(@{ Land_Measure_GTU_Month = '01'; Land_Measure_GTU1_301 = '1'; Land_Measure_GTU5_301 = '2' }) }
                'AF_Air_Land_Off_Process_DI_11_03' = @{ rows = @(@{ Land_Measure_GTU5_301 = '3'; Land_Measure_GTU5_O2 = '4' }) } } } }
            $orphans = @{}
            $plan = New-LandInputCellPlan -KindName 'Gtu' -DocumentName 'DOC-7' -Document $doc -ModelColumns $cols -SourceWithoutTarget $orphans
            "CELLS10=" + @($plan['DOC-7|202601|T10_03'] | ForEach-Object { $_.Cells }).Count
            "CELLS11=" + @($plan['DOC-7|202601|T11_03'] | ForEach-Object { $_.Cells }).Count
            "ORPHANS=" + (($orphans.Keys | Sort-Object | ForEach-Object { "$_=$($orphans[$_])" }) -join ';')
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandInputCells.Lib.ps1"), Path.Combine(Dir, "land-template.model.json"));

        Assert.True(exit == 0, output);
        Assert.Contains("T10_03=21 GTU5=0", output, StringComparison.Ordinal);
        Assert.Contains("T11_03=26 GTU5=5", output, StringComparison.Ordinal);
        Assert.Contains("CELLS10=2", output, StringComparison.Ordinal);
        Assert.Contains("CELLS11=2", output, StringComparison.Ordinal);
        Assert.Contains("ORPHANS=T10_03/Land_Measure_GTU5_301=1", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_не_має_дефолтного_шляху_AccountsFile_на_диску()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));

        Assert.Contains("[string]$AccountsFile,", build, StringComparison.Ordinal);
        Assert.DoesNotContain(@"F:\ecr-backup", build, StringComparison.Ordinal);
        Assert.Contains(@"Join-Path $repo '.sync-local\land-accounts.local.txt'", build, StringComparison.Ordinal);
    }}
