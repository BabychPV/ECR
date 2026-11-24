using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.MethodologyScriptMigration.Cli;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.MethodologyScriptMigration.Loading;
using Ecr.Scripting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>
/// Паритет на реальному SQL Server (MS, план §8.4): формульна версія (правила, константи за категорією й речовиною,
/// правило категорії, бібліотека, речовини) → перетворювач → тіньовий текст → <see cref="ParityRunner"/> дає 0 розбіжностей;
/// зміна однієї константи в тексті — розбіжність. Період 2099xx.
/// </summary>
[Collection("SqlServer")]
public sealed class ParityDbTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Scene(
        string MainCode, string LibraryCode, long DocumentId, int MainVersionId, int LibraryVersionId, string ZoneCode = "", string QtyCode = "",
        int PeriodKey = 0);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перетворена_й_затінена_версія_дає_нуль_розбіжностей_а_змінена_константа_дає_розбіжність()
    {
        var scene = await ArrangeAsync(209901);
        await ShadowAsync(scene);

        var equal = await RunAsync(scene);

        Assert.True(equal.IsEqual, string.Join("\n", equal.Mismatches.Select(m => $"{m.RowKey} {m.Aspect} {m.Old} / {m.New}")));
        Assert.Equal(1, equal.Targets);
        Assert.Equal(3, equal.RowsCompared); // North, South, North без кількості; East правилом не відібраний
        Assert.True(equal.ValuesCompared >= 4, $"values compared: {equal.ValuesCompared}"); // 2 речовини × (North, South) + проміжна Mass

        // Одна константа в тексті методології: 0301 → 0.51.
        await using (var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false))
        {
            var version = await db.MethodologyVersions.SingleAsync(v => v.Id == scene.MainVersionId);
            var changed = version.ScriptText!.Replace("\"0301\": 0.5,", "\"0301\": 0.51,", StringComparison.Ordinal);
            Assert.NotEqual(version.ScriptText, changed);
            version.SetScript(changed, ScriptCompiler.Hash(changed));
            await db.SaveChangesAsync();
        }

        var different = await RunAsync(scene);

        Assert.False(different.IsEqual);
        Assert.All(different.Mismatches, m => Assert.Equal(scene.MainCode, m.MethodologyCode));
        Assert.Contains(different.Mismatches, m => m.Aspect == ParityAspect.ValueNumber && m.RowKey is not null);
        Assert.DoesNotContain(different.Mismatches, m => m.Aspect is ParityAspect.RowSelected or ParityAspect.ValueMissing);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Parity_команда_пише_звіт_і_завершується_кодом_за_наявністю_розбіжностей()
    {
        var scene = await ArrangeAsync(209902);
        await ShadowAsync(scene);
        var directory = Path.Combine(Path.GetTempPath(), "ecr-ms-parity-" + Guid.NewGuid().ToString("N"));
        var report = Path.Combine(directory, "parity.json");

        var ok = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MainCode,
            "--document", scene.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--out", report);

        Assert.Equal(MigrationCli.Ok, ok.Code);
        Assert.Contains("розбіжностей 0", ok.Output, StringComparison.Ordinal);
        Assert.Contains("\"mismatches\": []", await File.ReadAllTextAsync(report), StringComparison.Ordinal);

        await using (var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false))
        {
            var version = await db.MethodologyVersions.SingleAsync(v => v.Id == scene.MainVersionId);
            var changed = version.ScriptText!.Replace("\"0304\": 0.7,", "\"0304\": 0.8,", StringComparison.Ordinal);
            Assert.NotEqual(version.ScriptText, changed);
            version.SetScript(changed, ScriptCompiler.Hash(changed));
            await db.SaveChangesAsync();
        }

        var failed = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MainCode, "--out", report);

        Assert.Equal(MigrationCli.Failures, failed.Code);
        Assert.Contains("value.number", failed.Output, StringComparison.Ordinal);
        Assert.Contains("\"aspect\": \"value.number\"", await File.ReadAllTextAsync(report), StringComparison.Ordinal);

        // Фільтр, що нічого не знаходить, — не «гаразд».
        var empty = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", "NO_SUCH_" + scene.MainCode, "--out", report);
        Assert.Equal(MigrationCli.Failures, empty.Code);
        Assert.Contains("НІЧОГО ПОРІВНЮВАТИ", empty.Output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ділення_на_нуль_у_Legacy_маскується_в_нуль_однаково_в_обох_шляхах()
    {
        var plain = await ArrangeAsync(209903);
        await ShadowAsync(plain);
        var baseline = await RunAsync(plain);

        var masked = await ArrangeAsync(209904, gap: true);
        await ShadowAsync(masked);
        var report = await RunAsync(masked);

        Assert.True(report.IsEqual, string.Join("; ", report.Mismatches.Select(m => $"{m.RowKey} {m.Aspect} {m.Old} / {m.New}")));
        Assert.True(baseline.IsProven);
        // Проміжне Gap = Qty / 0 → 0 у двох рядках з кількістю (North, South): +2 значення порівняно.
        Assert.Equal(baseline.ValuesCompared + 2, report.ValuesCompared);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_де_нічого_не_порівняно_не_дає_коду_0_навіть_без_розбіжностей()
    {
        var scene = await ArrangeAsync(209906, noMatch: true);
        await ShadowAsync(scene);
        var report = Path.Combine(Path.GetTempPath(), "ecr-ms-parity-" + Guid.NewGuid().ToString("N"), "parity.json");

        var run = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MainCode, "--out", report);

        var result = await RunAsync(scene);
        Assert.Equal(1, result.Targets);
        Assert.Equal(0, result.ValuesCompared);
        Assert.True(result.IsEqual);
        Assert.False(result.IsProven);
        Assert.Equal(MigrationCli.Failures, run.Code);
        Assert.Contains("НІЧОГО ПОРІВНЮВАТИ", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_яку_паритет_не_покриває_робить_результат_недоведеним_до_прапорця()
    {
        var scene = await ArrangeAsync(209907);
        await ShadowAsync(scene);
        await using (var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false))
        {
            // Старіша опублікована версія без тіньового тексту (не конвертувалась): паритет її не бачить.
            var methodologyId = (await db.MethodologyVersions.SingleAsync(v => v.Id == scene.MainVersionId)).MethodologyId;
            var older = new MethodologyVersion(methodologyId, "V0", CalculationLevel.Configuration, 1, Now);
            db.MethodologyVersions.Add(older);
            await db.SaveChangesAsync();
            older.Publish(1, "seed", new DateOnly(2098, 1, 1), true, Now);
            await db.SaveChangesAsync();
        }

        var report = Path.Combine(Path.GetTempPath(), "ecr-ms-parity-" + Guid.NewGuid().ToString("N"), "parity.json");
        var strict = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MainCode, "--out", report);
        var allowed = await CliAsync(
            "parity", "--connection", sql.ConnectionString, "--methodology", scene.MainCode, "--out", report, "--allow-unproven");

        Assert.Equal(MigrationCli.Failures, strict.Code);
        Assert.Contains("НЕДОВЕДЕНО " + scene.MainCode + " V0", strict.Output, StringComparison.Ordinal);
        Assert.Contains("no shadow text", strict.Output, StringComparison.Ordinal);
        Assert.Equal(MigrationCli.Ok, allowed.Code);
        Assert.Contains("\"unproven\"", await File.ReadAllTextAsync(report), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Затінена_версія_що_не_чинна_в_жодному_періоді_документів_названа_недоведеною_а_решта_зелена()
    {
        var scene = await ArrangeAsync(209908);
        await ShadowAsync(scene);
        await using (var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false))
        {
            var first = await db.MethodologyVersions.SingleAsync(v => v.Id == scene.MainVersionId);
            // Майбутня версія з тим самим текстом: період 2099 її не обирає, тож жодного екземпляра для неї не порівнюється.
            var future = new MethodologyVersion(first.MethodologyId, "V2", CalculationLevel.Configuration, 1, Now);
            db.MethodologyVersions.Add(future);
            await db.SaveChangesAsync();
            future.Publish(1, "seed", new DateOnly(2100, 1, 1), true, Now);
            future.SetScript(first.ScriptText!, ScriptCompiler.Hash(first.ScriptText!));
            await db.SaveChangesAsync();
        }

        var report = await RunAsync(scene);

        Assert.True(report.IsProven, string.Join("; ", report.Mismatches.Select(m => m.Aspect)));
        Assert.Equal(1, report.Targets);
        Assert.Equal(scene.MainCode + " V2", Assert.Single(report.UnprovenVersions).Split(':')[0]);

        var output = Path.Combine(Path.GetTempPath(), "ecr-ms-parity-" + Guid.NewGuid().ToString("N"), "parity.json");
        var cli = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MainCode, "--out", output);
        Assert.Equal(MigrationCli.Failures, cli.Code);
        Assert.Contains("НЕДОВЕДЕНО " + scene.MainCode + " V2", cli.Output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task D571_версія_з_екземплярами_але_без_жодного_значення_недоведена_й_не_потрапляє_в_перелік_перемикання()
    {
        // До D-571 така версія мала comparedForVersion = 1 і рахувалась доведеною поруч із будь-якою зеленою.
        var scene = await ArrangeAsync(209909, noMatch: true);
        await ShadowAsync(scene);

        var report = await RunAsync(scene);

        var main = Assert.Single(report.Versions, v => v.VersionId == scene.MainVersionId);
        Assert.Equal(1, main.Targets);
        Assert.Equal(0, main.ValuesCompared);
        Assert.False(main.IsProven);
        Assert.Contains(report.UnprovenVersions, u => u.StartsWith(scene.MainCode + " V1: no values compared", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Eligible, v => v.VersionId == scene.MainVersionId);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task D571_eligible_out_пише_доведені_версії_а_за_розбіжності_видаляє_файл()
    {
        var scene = await ArrangeAsync(209910);
        await ShadowAsync(scene);
        var directory = Path.Combine(Path.GetTempPath(), "ecr-ms-parity-" + Guid.NewGuid().ToString("N"));
        var report = Path.Combine(directory, "parity.json");
        var eligible = Path.Combine(directory, "eligible.json");
        var docId = scene.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var ok = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MainCode,
            "--document", docId, "--out", report, "--eligible-out", eligible);

        Assert.Equal(MigrationCli.Ok, ok.Code);
        var json = await File.ReadAllTextAsync(eligible);
        Assert.Contains("\"versionIds\": [\n    " + scene.MainVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture), json, StringComparison.Ordinal);

        await using (var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false))
        {
            var version = await db.MethodologyVersions.SingleAsync(v => v.Id == scene.MainVersionId);
            var changed = version.ScriptText!.Replace("\"0304\": 0.7,", "\"0304\": 0.8,", StringComparison.Ordinal);
            Assert.NotEqual(version.ScriptText, changed);
            version.SetScript(changed, ScriptCompiler.Hash(changed));
            await db.SaveChangesAsync();
        }

        var failed = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MainCode,
            "--document", docId, "--out", report, "--eligible-out", eligible, "--allow-unproven");

        Assert.Equal(MigrationCli.Failures, failed.Code);
        Assert.False(File.Exists(eligible), "after a mismatch the switch list must not survive");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task D571_parity_cases_доводить_версію_на_рядках_еталона_і_ловить_зміну_тексту()
    {
        var scene = await ArrangeAsync(209911);
        await ShadowAsync(scene);
        var directory = Path.Combine(Path.GetTempPath(), "ecr-ms-ref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cases = Path.Combine(directory, "cases.json");
        var eligible = Path.Combine(directory, "ref-eligible.json");
        var golden = Path.Combine(directory, "golden.json");
        var report = Path.Combine(directory, "ref-parity.json");
        var periodKey = scene.PeriodKey.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var docId = scene.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var versionId = scene.MainVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(cases, $$"""
            {"referenceDatabase":"ECR","documentId":{{docId}},"periodKey":{{periodKey}},"versions":[{"versionId":{{versionId}},"methodologyCode":"{{scene.MainCode}}","version":"V1","cases":[
              {"key":"REF001","table":"T","arguments":[{"argumentCode":"{{scene.ZoneCode}}","valueString":"North"},{"argumentCode":"{{scene.QtyCode}}","value":10}]},
              {"key":"REF002","table":"T","arguments":[{"argumentCode":"{{scene.ZoneCode}}","valueString":"South"},{"argumentCode":"{{scene.QtyCode}}","value":4}]},
              {"key":"REF003","table":"T","arguments":[{"argumentCode":"{{scene.ZoneCode}}","valueString":"North"}]}]}]}
            """);

        var ok = await CliAsync("parity-cases", "--connection", sql.ConnectionString, "--in", cases, "--out", report,
            "--eligible-out", eligible, "--golden-out", golden);

        Assert.True(ok.Code == MigrationCli.Ok, ok.Output + ok.Error);
        Assert.Contains("розбіжностей 0", ok.Output, StringComparison.Ordinal);
        var proven = await File.ReadAllTextAsync(eligible);
        Assert.Contains("\"source\": \"reference:ECR\"", proven, StringComparison.Ordinal);
        Assert.Contains("\"versionId\": " + versionId, proven, StringComparison.Ordinal);
        Assert.Contains("Emission@", await File.ReadAllTextAsync(golden), StringComparison.Ordinal);

        await using (var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false))
        {
            var version = await db.MethodologyVersions.SingleAsync(v => v.Id == scene.MainVersionId);
            var changed = version.ScriptText!.Replace("\"0301\": 0.5,", "\"0301\": 0.51,", StringComparison.Ordinal);
            Assert.NotEqual(version.ScriptText, changed);
            version.SetScript(changed, ScriptCompiler.Hash(changed));
            await db.SaveChangesAsync();
        }

        var failed = await CliAsync("parity-cases", "--connection", sql.ConnectionString, "--in", cases, "--out", report, "--eligible-out", eligible);

        Assert.Equal(MigrationCli.Failures, failed.Code);
        Assert.Contains("value.number", failed.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(eligible), "a mismatch must leave no switch list");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task D571_parity_cases_без_тіньового_тексту_рахує_очікування_старим_рушієм_і_нічого_не_доводить()
    {
        // P-1123-6 (C11_15): у чинної версії немає правил -> тіньового тексту немає; очікування нової версії все одно — вихід
        // старого рушія на синтетичних рядках, а перелік перемикання цю версію не містить.
        var scene = await ArrangeAsync(209912);
        var directory = Path.Combine(Path.GetTempPath(), "ecr-ms-syn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cases = Path.Combine(directory, "cases.json");
        var golden = Path.Combine(directory, "golden.json");
        var eligible = Path.Combine(directory, "eligible.json");
        var periodKey = scene.PeriodKey.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var docId = scene.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var versionId = scene.MainVersionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await File.WriteAllTextAsync(cases, $$"""
            {"referenceDatabase":"synthetic","documentId":{{docId}},"periodKey":{{periodKey}},"versions":[{"versionId":{{versionId}},"methodologyCode":"{{scene.MainCode}}","version":"V1","cases":[
              {"key":"SYN_R01","table":"SYN","arguments":[{"argumentCode":"{{scene.ZoneCode}}","valueString":"North"},{"argumentCode":"{{scene.QtyCode}}","value":10}]},
              {"key":"SYN_V01","table":"SYN","arguments":[{"argumentCode":"{{scene.ZoneCode}}","valueString":"South"},{"argumentCode":"{{scene.QtyCode}}","value":4}]}]}]}
            """);

        var run = await CliAsync("parity-cases", "--connection", sql.ConnectionString, "--in", cases, "--out", Path.Combine(directory, "report.json"),
            "--eligible-out", eligible, "--golden-out", golden, "--golden-all");

        Assert.Equal(MigrationCli.Failures, run.Code); // нічого не доведено
        Assert.Contains("no shadow text (old engine only", run.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(eligible));
        var json = await File.ReadAllTextAsync(golden);
        Assert.Contains("\"key\": \"SYN_V01\"", json, StringComparison.Ordinal); // --golden-all: кожен рядок із виходами
        Assert.Contains("Emission@", json, StringComparison.Ordinal);
    }

    // ───── стенд ─────

    private async Task<ParityRunReport> RunAsync(Scene scene)
    {
        await using var provider = ParityHost.Build(sql.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ParityRunner>().RunAsync(scene.MainCode, scene.DocumentId, default);
    }

    private static async Task<(int Code, string Output, string Error)> CliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await MigrationCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>Перетворює обидві версії (бібліотеку й основну) справжнім конвертером і записує тіньові тексти.</summary>
    private async Task ShadowAsync(Scene scene)
    {
        await using var read = MigrationCli.CreateContext(sql.ConnectionString, readOnly: true);
        var loader = new VersionSourceLoader(read, MigrationCli.CreateFormulaEngine());
        var texts = new Dictionary<int, string>();
        foreach (var versionId in new[] { scene.LibraryVersionId, scene.MainVersionId })
        {
            var loaded = await loader.LoadAsync(versionId, default);
            Assert.False(loaded.HasErrors, string.Join("; ", loaded.Issues.Select(i => i.Code + ": " + i.Message)));
            var converted = ScriptConverter.Convert(loaded.Source);
            Assert.False(converted.HasErrors, string.Join("; ", converted.Issues.Select(i => i.Code + ": " + i.Message)));
            texts[versionId] = converted.Text!;
        }

        await using var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false);
        foreach (var (versionId, text) in texts)
        {
            var version = await db.MethodologyVersions.SingleAsync(v => v.Id == versionId);
            version.SetScript(text, ScriptCompiler.Hash(text));
        }

        await db.SaveChangesAsync();
    }

    private async Task<Scene> ArrangeAsync(int periodKey, bool gap = false, bool noMatch = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: periodKey, columnCount: 3, rowCount: 4);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        await using var db = builder.CreateContext();
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.AdvanceTo(PeriodState.Open, Now);

        var columns = await db.ColumnDefs
            .Where(c => document.ColumnDefIds.Contains(c.Id))
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.Id, c.Code })
            .ToListAsync();
        var zone = columns[0];
        var qty = columns[1];
        var output = columns[2];

        // Рядки: North/10, South/4, East/7 (правилом не відібраний), North без кількості.
        var cells = new (string Zone, decimal? Qty)[] { ("North", 10m), ("South", 4m), ("East", 7m), ("North", null) };
        for (var i = 0; i < cells.Length; i++)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, document.RowIds[i], zone.Id),
                document.TableDefId, new CellValueData { ValueString = cells[i].Zone }));
            if (cells[i].Qty is { } value)
            {
                db.CellValues.Add(new CellValue(
                    new CellAddress(document.PeriodKey, document.RowIds[i], qty.Id),
                    document.TableDefId, new CellValueData { ValueNumeric = value }));
            }
        }

        // Речовини: код 0301/0304 живе в полі SUBSTANCE_CODE.
        var substances = new RegistryDef(EcrCode.Create("PSUBST_" + tag), Name("substance"), false);
        db.RegistryDefs.Add(substances);
        await db.SaveChangesAsync();
        var e301 = new RegistryEntry(substances.Id, EcrCode.Create("E301"), Name("NO2"));
        var e304 = new RegistryEntry(substances.Id, EcrCode.Create("E304"), Name("NO"));
        db.RegistryEntries.AddRange(e301, e304);
        var codeField = new RegistryFieldDef(substances.Id, EcrCode.Create("SUBSTANCE_CODE"), Name("code"), CellDataType.String, 1);
        db.RegistryFieldDefs.Add(codeField);
        await db.SaveChangesAsync();
        var v301 = new RegistryValue(e301.Id, codeField.Id);
        v301.Set(CellDataType.String, "0301", null);
        var v304 = new RegistryValue(e304.Id, codeField.Id);
        v304.Set(CellDataType.String, "0304", null);
        db.RegistryValues.AddRange(v301, v304);

        var unitT = (await db.Units.FirstAsync(u => u.Code == "t")).Id;
        var unitKg = (await db.Units.FirstAsync(u => u.Code == "kg")).Id;

        var libraryCode = "PLIB_" + tag;
        var mainCode = "PMAIN_" + tag;
        var library = new Methodology(EcrCode.Create(libraryCode), Name("library"));
        library.SetKind(MethodologyKind.Library);
        var main = new Methodology(EcrCode.Create(mainCode), Name("main"));
        db.Methodologies.AddRange(library, main);
        await db.SaveChangesAsync();

        var libVersion = new MethodologyVersion(library.Id, "V1", CalculationLevel.Configuration, 1, Now);
        var mainVersion = new MethodologyVersion(main.Id, "V1", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.AddRange(libVersion, mainVersion);
        await db.SaveChangesAsync();

        // Бібліотека: Dens = K * 2, K за категорією.
        var dens = new MethodologyFormula(libVersion.Id, EcrCode.Create("Dens"), "CST.K * 2");
        dens.SetEvaluationOrder(1);
        db.MethodologyFormulas.Add(dens);
        db.MethodologyCategories.AddRange(
            new MethodologyCategory(libVersion.Id, "A", 1), new MethodologyCategory(libVersion.Id, "B", 2));
        AddConstant(db, libVersion.Id, "K", 1.5m, unitKg, category: "A");
        AddConstant(db, libVersion.Id, "K", 2.5m, unitKg, category: "B");

        // Основна: імпорт, проміжна Mass, вихід Emission по речовинах, категорія за зоною, EF за речовиною.
        db.MethodologyImports.Add(new MethodologyImport(mainVersion.Id, library.Id, main.Id));
        var mass = new MethodologyFormula(mainVersion.Id, EcrCode.Create("Mass"), $"@{qty.Code} * 2");
        mass.SetScope(MethodologyFormulaScope.Row);
        mass.SetVisible(true);
        mass.SetOutputUnit(unitT);
        mass.SetEvaluationOrder(1);
        var emission = new MethodologyFormula(mainVersion.Id, EcrCode.Create("Emission"), "!Mass * CST.EF * !Dens");
        emission.SetEvaluationOrder(2);
        db.MethodologyFormulas.AddRange(mass, emission);
        if (gap)
        {
            // Qty / 0: у Legacy NaN/∞ → 0 (маскування старого рушія), скрипт мусить дати те саме.
            var zeroDivision = new MethodologyFormula(mainVersion.Id, EcrCode.Create("Gap"), $"@{qty.Code} / 0");
            zeroDivision.SetScope(MethodologyFormulaScope.Row);
            zeroDivision.SetVisible(true);
            zeroDivision.SetOutputUnit(unitT);
            zeroDivision.SetEvaluationOrder(3);
            db.MethodologyFormulas.Add(zeroDivision);
        }

        db.MethodologyOutputs.Add(new MethodologyOutput(mainVersion.Id, EcrCode.Create("Emission"), unitT, 1));
        db.MethodologyCategories.AddRange(
            new MethodologyCategory(mainVersion.Id, "A", 1), new MethodologyCategory(mainVersion.Id, "B", 2));
        db.MethodologyCategoryRules.Add(new MethodologyCategoryRule(mainVersion.Id, $"if(@{zone.Code} = 'North', 'A', 'B')", Now));
        db.MethodologySubstances.AddRange(
            new MethodologySubstance(mainVersion.Id, e301.Id, 1), new MethodologySubstance(mainVersion.Id, e304.Id, 2));
        AddConstant(db, mainVersion.Id, "EF", 0.5m, unitKg, substanceEntryId: e301.Id);
        // Унікальний індекс констант не бачить речовини: другу розрізняємо початком чинності (до життя версії).
        AddConstant(db, mainVersion.Id, "EF", 0.7m, unitKg, substanceEntryId: e304.Id, validFrom: new DateOnly(2000, 1, 1));
        db.MethodologyRules.AddRange(
            new MethodologyRule(mainVersion.Id, EcrCode.Create("R_NORTH"), $"{{\"{zone.Id}\":\"{(noMatch ? "Nowhere" : "North")}\"}}", 1),
            new MethodologyRule(mainVersion.Id, EcrCode.Create("R_SOUTH"), $"{{\"{zone.Id}\":\"{(noMatch ? "Elsewhere" : "South")}\"}}", 2));
        await db.SaveChangesAsync();

        db.CalculationBindings.Add(new CalculationBinding(document.TableDefId, output.Id, main.Id, "Emission", "{}"));
        libVersion.Publish(1, "seed", new DateOnly(2099, 1, 1), true, Now);
        mainVersion.Publish(1, "seed", new DateOnly(2099, 1, 1), true, Now);
        await db.SaveChangesAsync();

        return new Scene(mainCode, libraryCode, document.DocumentId, mainVersion.Id, libVersion.Id, zone.Code, qty.Code, periodKey);
    }

    private static void AddConstant(
        EcrDbContext db, int versionId, string code, decimal value, int unitId, string? category = null, long? substanceEntryId = null, DateOnly? validFrom = null)
    {
        var constant = new MethodologyConstant(versionId, EcrCode.Create(code), value, unitId);
        constant.SetScope(category, substanceEntryId);
        constant.SetValidity(validFrom, null);
        db.MethodologyConstants.Add(constant);
    }
}
