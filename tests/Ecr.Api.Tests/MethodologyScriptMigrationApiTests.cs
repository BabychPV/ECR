using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.MethodologyScriptMigration.Cli;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.MethodologyScriptMigration.Loading;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Перенесення методологій на скрипт НАСКРІЗЬ на справжньому SQL Server і черзі (MS 8.5): формульна методологія з
/// бібліотекою й імпортом → справжній перетворювач інструмента (<c>ScriptConverter</c>, відбиток джерела) →
/// <c>POST …/script-migration/apply</c> (сухий прогін, відбиток не збігся, запис) → <c>switch</c> на Script
/// (відмови групи й причини, успіх) → ПЕРЕРАХУНОК дає ті самі числа → <c>switch</c> назад → ті самі числа.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально): у <c>SwitchScriptEngineHandler</c> прибрати <c>RequireCompleteGroupAsync</c> — червоніє
/// відмова групи; у <c>ApplyScriptPackageHandler</c> звіряти відбиток із пакетом, а не з базою — червоніє «відбиток не
/// збігся»; у <c>ScriptMaterializer.DiffAgainstStoredAsync</c> повертати порожньо — перемикання проходить попри
/// інший склад (див. модульні тести). Періоди — 2099xx.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyScriptMigrationApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Script-Migration-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly DateTime Now = new(2099, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Stand(
        TestDocumentBuilder Builder,
        TestDocument Document,
        int PeriodKey,
        string TableCode,
        string OutputCode,
        int LibraryId,
        int LibraryVersionId,
        string LibraryCode,
        int MainId,
        int MainVersionId,
        string MainCode,
        IReadOnlyList<string> RowKeys);

    private sealed record Users(HttpClient Publisher, HttpClient Viewer, HttpClient Worker);

    private const string Url = "/api/v1/methodologies/script-migration";

    // ───────────────────────────── сценарій ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Пакет_apply_потім_switch_на_Script_дає_ті_самі_числа_і_switch_назад_теж()
    {
        var s = await ArrangeAsync(209907).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var u = await SignInAllAsync(app, s).ConfigureAwait(true);

        // Базова лінія: формули рахують, як рахували.
        await RecalculateAsync(app, u.Worker, s).ConfigureAwait(true);
        var baseline = await ResultsAsync(u.Worker, s).ConfigureAwait(true);
        Assert.Contains(baseline, x => x.StartsWith(s.OutputCode, StringComparison.Ordinal));

        // Справжній перетворювач інструмента + відбиток джерела.
        var main = await ConvertAsync(s.MainVersionId).ConfigureAwait(true);
        var library = await ConvertAsync(s.LibraryVersionId).ConfigureAwait(true);
        Assert.StartsWith("//", main.Text, StringComparison.Ordinal);

        // Імпортер першим, бібліотека потім: порядок у пакеті не важить.
        object Package(string mainFingerprint) => new
        {
            format = "ecr-methodology-script-package",
            formatVersion = 3,
            entries = new object[]
            {
                new { methodologyCode = s.MainCode, version = "V1", effectiveFrom = "2099-01-01", text = main.Text, sourceFingerprint = mainFingerprint },
                new { methodologyCode = s.LibraryCode, version = "V1", effectiveFrom = "2099-01-01", text = library.Text, sourceFingerprint = library.Fingerprint },
            },
        };

        // 1. Сухий прогін: обидві версії пройшли б, у базі нічого.
        var dry = await PostAsync(app, u.Publisher, Url + "/apply?dryRun=true", Package(main.Fingerprint), HttpStatusCode.OK).ConfigureAwait(true);
        Assert.True(dry.GetProperty("dryRun").GetBoolean());
        Assert.True(Statuses(dry).All(x => x == "wouldApply"), dry.ToString());
        Assert.Equal((null, null), await ShadowAsync(s).ConfigureAwait(true));

        // 2. Відбиток джерела не збігся: ця версія відхилена з причиною, решта пройшла.
        var mismatch = await PostAsync(app, u.Publisher, Url + "/apply?dryRun=false", Package(new string('0', 64)), HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(["refused", "applied"], Statuses(mismatch));
        Assert.Equal(
            "scriptMigration.refused.fingerprintMismatch",
            mismatch.GetProperty("entries")[0].GetProperty("reason").GetString());
        var shadow = await ShadowAsync(s).ConfigureAwait(true);
        Assert.Null(shadow.Main);
        Assert.Equal(library.Text, shadow.Library);

        // 3. Запис: тінь лежить, рушій не змінився, у журналі є подія з причиною.
        var applied = await PostAsync(app, u.Publisher, Url + "/apply?dryRun=false", Package(main.Fingerprint), HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(["applied", "applied"], Statuses(applied));
        shadow = await ShadowAsync(s).ConfigureAwait(true);
        Assert.Equal((main.Text, library.Text), (shadow.Main, shadow.Library));
        Assert.Equal([MethodologyEngine.Formulas, MethodologyEngine.Formulas], await EnginesAsync(s).ConfigureAwait(true));
        Assert.True(await AuditCountAsync(s.MainVersionId, "ScriptMigration.apply").ConfigureAwait(true) >= 1);

        // Числа ще формульні — ті самі.
        await RecalculateAsync(app, u.Worker, s).ConfigureAwait(true);
        Assert.Equal(baseline, await ResultsAsync(u.Worker, s).ConfigureAwait(true));

        // 4. Відмови перемикання: група, причина, права.
        var onlyMain = await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.MainVersionId }, engine = "Script", reason = "cutover" },
            HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.switchGroupIncomplete", onlyMain.GetProperty("messageKey").GetString());
        Assert.Contains(s.LibraryCode, Assert.Single(onlyMain.GetProperty("missing").EnumerateArray()).GetString(), StringComparison.Ordinal);

        var onlyLibrary = await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.LibraryVersionId }, engine = "Script", reason = "cutover" },
            HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.switchGroupIncomplete", onlyLibrary.GetProperty("messageKey").GetString());
        Assert.Contains(s.MainCode, Assert.Single(onlyLibrary.GetProperty("missing").EnumerateArray()).GetString(), StringComparison.Ordinal);

        var noReason = await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.MainVersionId, s.LibraryVersionId }, engine = "Script", reason = " " },
            HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.switchReasonRequired", noReason.GetProperty("messageKey").GetString());
        Assert.Equal([MethodologyEngine.Formulas, MethodologyEngine.Formulas], await EnginesAsync(s).ConfigureAwait(true));

        using (var forbidden = await u.Viewer.PostAsJsonAsync(
                   new Uri(Url + "/switch", UriKind.Relative),
                   new { versionIds = new[] { s.MainVersionId }, engine = "Script", reason = "x" }).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var forbiddenApply = await u.Viewer.PostAsJsonAsync(
                   new Uri(Url + "/apply?dryRun=true", UriKind.Relative), Package(main.Fingerprint)).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenApply.StatusCode);
        }

        // 5. Перемикання групи на Script: рушії, журнал із причиною.
        var switched = await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.MainVersionId, s.LibraryVersionId }, engine = "Script", reason = "планова заміна" },
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("Script", switched.GetProperty("engine").GetString());
        Assert.Equal([MethodologyEngine.Script, MethodologyEngine.Script], await EnginesAsync(s).ConfigureAwait(true));
        Assert.True(await AuditCountAsync(s.MainVersionId, "планова заміна").ConfigureAwait(true) >= 1);

        // 6. Перерахунок на скрипті дає ТІ САМІ числа.
        await RecalculateAsync(app, u.Worker, s).ConfigureAwait(true);
        Assert.Equal(baseline, await ResultsAsync(u.Worker, s).ConfigureAwait(true));

        // 7. Назад на формули: рушії, таблиці читання зняті, числа ті самі.
        var back = await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.MainVersionId, s.LibraryVersionId }, engine = "Formulas", reason = "відкат" },
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("Formulas", back.GetProperty("engine").GetString());
        Assert.Equal([MethodologyEngine.Formulas, MethodologyEngine.Formulas], await EnginesAsync(s).ConfigureAwait(true));
        await using (var db = Context())
        {
            Assert.Empty(await db.MethodologyReadTables.AsNoTracking()
                .Where(t => t.MethodologyVersionId == s.MainVersionId).ToListAsync().ConfigureAwait(true));
        }

        await RecalculateAsync(app, u.Worker, s).ConfigureAwait(true);
        Assert.Equal(baseline, await ResultsAsync(u.Worker, s).ConfigureAwait(true));
    }

    /// <summary>
    /// F13a: Formulas → Script → Formulas повертає ті самі ребра методологій і довідників. Скрипт при матеріалізації
    /// переписує їх за своїм текстом (тут — додаткове ребро до чужої методології й ребро довідника, як дав би скрипт, що
    /// читає чужі результати й довідники); без перебудови за формулами вони лишалися б після повернення на формули.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Switch_Formulas_Script_Formulas_повертає_ті_самі_ребра_методологій_і_довідників()
    {
        var s = await ArrangeAsync(209909).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var u = await SignInAllAsync(app, s).ConfigureAwait(true);

        // Стан «до»: ребро main → library (його пише публікація формул для посилання !Dens з імпорту).
        int foreign;
        int registryId;
        await using (var db = Context())
        {
            var other = new Methodology(EcrCode.Create("MSXO" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()), Name("foreign"));
            db.Methodologies.Add(other);
            db.MethodologyDependencies.Add(new MethodologyDependency(s.MainId, s.LibraryId));
            await db.SaveChangesAsync().ConfigureAwait(true);
            foreign = other.Id;
            registryId = (await db.RegistryDefs.AsNoTracking().OrderByDescending(r => r.Id).FirstAsync().ConfigureAwait(true)).Id;
        }

        var before = await EdgesAsync(s, foreign, registryId).ConfigureAwait(true);
        Assert.Equal(["dep:main->library"], before);

        var main = await ConvertAsync(s.MainVersionId).ConfigureAwait(true);
        var library = await ConvertAsync(s.LibraryVersionId).ConfigureAwait(true);
        var package = new
        {
            format = "ecr-methodology-script-package",
            formatVersion = 3,
            entries = new object[]
            {
                new { methodologyCode = s.MainCode, version = "V1", effectiveFrom = "2099-01-01", text = main.Text, sourceFingerprint = main.Fingerprint },
                new { methodologyCode = s.LibraryCode, version = "V1", effectiveFrom = "2099-01-01", text = library.Text, sourceFingerprint = library.Fingerprint },
            },
        };
        await PostAsync(app, u.Publisher, Url + "/apply?dryRun=false", package, HttpStatusCode.OK).ConfigureAwait(true);
        await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.MainVersionId, s.LibraryVersionId }, engine = "Script", reason = "туди" },
            HttpStatusCode.OK).ConfigureAwait(true);

        // Скрипт «читає» чужу методологію й довідник: такі ребра лягли б при його матеріалізації.
        await using (var db = Context())
        {
            db.MethodologyDependencies.Add(new MethodologyDependency(s.MainId, foreign));
            db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(s.MainVersionId, "script", registryId, fieldPath: null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        Assert.Equal(3, (await EdgesAsync(s, foreign, registryId).ConfigureAwait(true)).Count);

        await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.MainVersionId, s.LibraryVersionId }, engine = "Formulas", reason = "назад" },
            HttpStatusCode.OK).ConfigureAwait(true);

        Assert.Equal(before, await EdgesAsync(s, foreign, registryId).ConfigureAwait(true));
    }

    private async Task<List<string>> EdgesAsync(Stand s, int foreign, int registryId)
    {
        await using var db = Context();
        var deps = await db.MethodologyDependencies.AsNoTracking()
            .Where(d => d.FromMethodologyId == s.MainId || d.FromMethodologyId == s.LibraryId).ToListAsync().ConfigureAwait(false);
        var uses = await db.RegistryUses.AsNoTracking()
            .Where(r => r.SourceKind == RegistryUse.MethodologyVersionSource
                        && (r.SourceId == s.MainVersionId || r.SourceId == s.LibraryVersionId))
            .ToListAsync().ConfigureAwait(false);
        string Name(int id) => id == s.MainId ? "main" : id == s.LibraryId ? "library" : id == foreign ? "foreign" : id.ToString(CultureInfo.InvariantCulture);
        return
        [
            .. deps.Select(d => $"dep:{Name(d.FromMethodologyId)}->{Name(d.ToMethodologyId)}"),
            .. uses.Select(r => $"use:{(r.SourceId == s.MainVersionId ? "main" : "library")}:{r.FormulaCode}->{(r.RegistryDefId == registryId ? "registry" : r.RegistryDefId.ToString(CultureInfo.InvariantCulture))}"),
        ];
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-8.5")]
    public async Task Switch_на_Script_відмовляє_коли_склад_зі_скрипта_не_збігається_з_наявним_і_нічого_не_лишає()
    {
        var s = await ArrangeAsync(209908).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var u = await SignInAllAsync(app, s).ConfigureAwait(true);

        // Скрипт, що дає інший вихід, ніж лежить у версії (OUT_OTHER замість справжнього).
        var library = await ConvertAsync(s.LibraryVersionId).ConfigureAwait(true);
        var main = await ConvertAsync(s.MainVersionId).ConfigureAwait(true);
        var wrong = main.Text.Replace("output " + s.OutputCode, "output " + s.OutputCode + "X", StringComparison.Ordinal);
        Assert.NotEqual(main.Text, wrong);
        await using (var db = Context())
        {
            var versions = await db.MethodologyVersions
                .Where(v => v.Id == s.MainVersionId || v.Id == s.LibraryVersionId).ToListAsync().ConfigureAwait(true);
            versions.Single(v => v.Id == s.MainVersionId).SetScript(wrong, Ecr.Scripting.ScriptCompiler.Hash(wrong));
            versions.Single(v => v.Id == s.LibraryVersionId).SetScript(library.Text, Ecr.Scripting.ScriptCompiler.Hash(library.Text));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var refused = await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.MainVersionId, s.LibraryVersionId }, engine = "Script", reason = "cutover" },
            HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);

        Assert.Equal("err.ECR-CALC-0422.switchChecksFailed", refused.GetProperty("messageKey").GetString());
        var keys = refused.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("messageKey").GetString()).ToList();
        Assert.Contains("switch.problem.outputsDiffer", keys);

        // Транзакція відкочена: рушії не змінилися, таблиць читання й залежностей немає.
        Assert.Equal([MethodologyEngine.Formulas, MethodologyEngine.Formulas], await EnginesAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await AuditCountAsync(s.MainVersionId, "cutover").ConfigureAwait(true));
    }

    /// <summary>
    /// MS x B179 (закриття A1): перемикання Formulas - Script, після якого скрипт читає довідник, який наповнює шаблон його
    /// ж виходу (цикл шаблон - довідник - методологія - шаблон), - 422 <c>ECR-TMPL-0422</c> із полем <c>cycle</c>,
    /// рушії не змінені; той самий стенд без читання довідника перемикається.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B179")]
    public async Task Switch_на_Script_що_замикає_цикл_залежностей_документів_відмовляє_422_а_без_циклу_проходить()
    {
        var s = await ArrangeAsync(209906).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var u = await SignInAllAsync(app, s).ConfigureAwait(true);

        var registryCode = "B179SW" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        string templateCode;
        await using (var db = Context())
        {
            var registry = new RegistryDef(EcrCode.Create(registryCode), Name(registryCode), isTemporal: false);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync().ConfigureAwait(true);
            var fields = new List<RegistryFieldDef>();
            foreach (var (code, type, ordinal) in new[]
                     {
                         ("YEAR", CellDataType.Int, 1), ("MONTH", CellDataType.Int, 2), ("STREAM", CellDataType.String, 3),
                         ("INDICATOR", CellDataType.String, 4), ("VALUE", CellDataType.Decimal, 5),
                     })
            {
                var field = new RegistryFieldDef(registry.Id, EcrCode.Create(code), Name(code), type, ordinal);
                field.Update(Name(code), ordinal, ordinal < 5);
                db.RegistryFieldDefs.Add(field);
                fields.Add(field);
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            db.RegistryKeyDefs.Add(new RegistryKeyDef(
                registry.Id, EcrCode.Create("PK"), Name("PK"), [.. fields.Take(4)], isPrimary: true, ignoreCase: true, 1, Now));
            db.DocumentSourceBindings.Add(new DocumentSourceBinding("B179SW_" + registryCode, s.Document.TemplateId, registry.Id));
            await db.SaveChangesAsync().ConfigureAwait(true);
            templateCode = await db.Templates.Where(t => t.Id == s.Document.TemplateId).Select(t => t.Code).SingleAsync().ConfigureAwait(true);
        }

        var main = await ConvertAsync(s.MainVersionId).ConfigureAwait(true);
        var library = await ConvertAsync(s.LibraryVersionId).ConfigureAwait(true);
        var lines = main.Text.ReplaceLineEndings(Environment.NewLine).Split(Environment.NewLine).ToList();
        var at = lines.FindIndex(l => l.TrimStart().StartsWith("output ", StringComparison.Ordinal));
        Assert.True(at >= 0, main.Text);
        lines.Insert(at, $"var reg = REGFIELD(REGFIND(\"{registryCode}\", period.Year, period.Month, \"a\", \"b\"), \"VALUE\");");
        var cyclic = string.Join(Environment.NewLine, lines);

        await SetShadowAsync(s, cyclic, library.Text).ConfigureAwait(true);
        var refused = await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.MainVersionId, s.LibraryVersionId }, engine = "Script", reason = "cycle" },
            HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("ECR-TMPL-0422", refused.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-TMPL-0422.dependencyCycle", refused.GetProperty("messageKey").GetString());
        Assert.Equal(
            $"{s.MainCode} → {templateCode} → {registryCode} → {s.MainCode}",
            refused.GetProperty("cycle").GetString());
        Assert.Equal([MethodologyEngine.Formulas, MethodologyEngine.Formulas], await EnginesAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await AuditCountAsync(s.MainVersionId, "cycle").ConfigureAwait(true));

        // Без читання довідника (зв'язка джерела є) - перемикання проходить.
        await SetShadowAsync(s, main.Text, library.Text).ConfigureAwait(true);
        await PostAsync(
            app, u.Publisher, Url + "/switch",
            new { versionIds = new[] { s.MainVersionId, s.LibraryVersionId }, engine = "Script", reason = "no cycle" },
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal([MethodologyEngine.Script, MethodologyEngine.Script], await EnginesAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-8.4")]
    public async Task Parity_endpoint_дає_нуль_розбіжностей_на_перетвореній_версії_і_розбіжність_після_зміни_тексту()
    {
        var s = await ArrangeAsync(209909).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var u = await SignInAllAsync(app, s).ConfigureAwait(true);
        var checker = await SignInAsync(app, ["Calculation.View", "Calculation.Publish", "Document.View"], s, GrantLevel.Write).ConfigureAwait(true);
        var url = $"/api/v1/methodologies/{s.MainId}/versions/{s.MainVersionId}/script/parity";
        var body = new { documentId = s.Document.DocumentId, periodKey = s.PeriodKey };

        // Тіньові тексти справжнього перетворювача (рушій лишається Formulas).
        var main = await ConvertAsync(s.MainVersionId).ConfigureAwait(true);
        var library = await ConvertAsync(s.LibraryVersionId).ConfigureAwait(true);
        await SetShadowAsync(s, main.Text, library.Text).ConfigureAwait(true);

        var equal = await PostAsync(app, checker, url, body, HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(0, equal.GetProperty("mismatchCount").GetInt32());
        Assert.Equal(1, equal.GetProperty("targets").GetInt32());
        Assert.Equal(2, equal.GetProperty("rowsCompared").GetInt32()); // North ×2; South правилом не відібраний
        Assert.True(equal.GetProperty("valuesCompared").GetInt32() >= 2, equal.ToString());
        Assert.Empty(equal.GetProperty("mismatches").EnumerateArray());

        // Прав немає — відмова; нічого не записується (рушій той самий).
        using (var forbidden = await u.Viewer.PostAsJsonAsync(new Uri(url, UriKind.Relative), body).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        // Змінена константа в збереженому тексті: розбіжність за числом.
        var changed = main.Text.Replace("0.5", "0.6", StringComparison.Ordinal);
        Assert.NotEqual(main.Text, changed);
        await SetShadowAsync(s, changed, library.Text).ConfigureAwait(true);

        var different = await PostAsync(app, checker, url, body, HttpStatusCode.OK).ConfigureAwait(true);
        Assert.True(different.GetProperty("mismatchCount").GetInt32() > 0, different.ToString());
        Assert.Contains(
            different.GetProperty("mismatches").EnumerateArray(),
            m => m.GetProperty("aspect").GetString() == "value.number" && m.GetProperty("rowKey").ValueKind == JsonValueKind.String);
        Assert.Equal([MethodologyEngine.Formulas, MethodologyEngine.Formulas], await EnginesAsync(s).ConfigureAwait(true));

        // Чужа версія за адресою методологи — 404.
        var foreign = await PostAsync(
            app, checker, $"/api/v1/methodologies/{s.LibraryId}/versions/{s.MainVersionId}/script/parity", body, HttpStatusCode.NotFound)
            .ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0404.version", foreign.GetProperty("messageKey").GetString());
    }

    private async Task SetShadowAsync(Stand s, string mainText, string libraryText)
    {
        await using var db = Context();
        var versions = await db.MethodologyVersions
            .Where(v => v.Id == s.MainVersionId || v.Id == s.LibraryVersionId).ToListAsync().ConfigureAwait(false);
        versions.Single(v => v.Id == s.MainVersionId).SetScript(mainText, Ecr.Scripting.ScriptCompiler.Hash(mainText));
        versions.Single(v => v.Id == s.LibraryVersionId).SetScript(libraryText, Ecr.Scripting.ScriptCompiler.Hash(libraryText));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    // ───────────────────────────── стенд ─────────────────────────────

    /// <summary>Формульна методологія-бібліотека з константою й методологія з імпортом, правилом і виходом.</summary>
    private async Task<Stand> ArrangeAsync(int periodKey)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey, columnCount: 2, rowCount: 3, rowMode: TableRowMode.Fixed).ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        await using var db = builder.CreateContext();
        var table = await db.TableDefs.SingleAsync(t => t.Id == document.TableDefId).ConfigureAwait(false);
        var columns = await db.ColumnDefs.AsNoTracking()
            .Where(c => document.ColumnDefIds.Contains(c.Id)).OrderBy(c => c.Ordinal).ToListAsync().ConfigureAwait(false);
        var rowKeys = await db.RowDefs.AsNoTracking()
            .Where(r => r.TableDefId == document.TableDefId).OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue)
            .ToListAsync().ConfigureAwait(false);

        var outputCode = "EMIS" + tag;
        var calculated = new ColumnDef(table.Id, EcrCode.Create(outputCode), Name(outputCode), 20, CellDataType.Calculated);
        db.ColumnDefs.Add(calculated);

        var templateVersion = await db.TemplateVersions.SingleAsync(v => v.Id == document.TemplateVersionId).ConfigureAwait(false);
        templateVersion.Publish(1, Now);

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(Now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, Now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Дані: дві північні й одна південна.
        var kinds = new[] { ("North", 10m), ("North", 5m), ("South", 7m) };
        for (var i = 0; i < kinds.Length; i++)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, document.RowIds[i], columns[0].Id),
                document.TableDefId, new CellValueData { ValueString = kinds[i].Item1 }));
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, document.RowIds[i], columns[1].Id),
                document.TableDefId, new CellValueData { ValueNumeric = kinds[i].Item2 }));
        }

        var unitT = (await db.Units.FirstAsync(u => u.Code == "t").ConfigureAwait(false)).Id;
        var unitKg = (await db.Units.FirstAsync(u => u.Code == "kg").ConfigureAwait(false)).Id;

        var libraryCode = "MSXL" + tag;
        var mainCode = "MSXM" + tag;
        var library = new Methodology(EcrCode.Create(libraryCode), Name("library"));
        library.SetKind(MethodologyKind.Library);
        var main = new Methodology(EcrCode.Create(mainCode), Name("main"));
        db.Methodologies.AddRange(library, main);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var libraryVersion = new MethodologyVersion(library.Id, "V1", CalculationLevel.Configuration, 1, Now);
        var mainVersion = new MethodologyVersion(main.Id, "V1", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.AddRange(libraryVersion, mainVersion);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var dens = new MethodologyFormula(libraryVersion.Id, EcrCode.Create("Dens"), "CST.K * 2");
        dens.SetEvaluationOrder(1);
        db.MethodologyFormulas.Add(dens);
        AddConstant(db, libraryVersion.Id, "K", 1.5m, unitKg);

        db.MethodologyImports.Add(new MethodologyImport(mainVersion.Id, library.Id, main.Id));
        var mass = new MethodologyFormula(mainVersion.Id, EcrCode.Create("Mass"), $"@{columns[1].Code} * 2");
        mass.SetScope(MethodologyFormulaScope.Row);
        mass.SetVisible(true);
        mass.SetOutputUnit(unitT);
        mass.SetEvaluationOrder(1);
        var emission = new MethodologyFormula(mainVersion.Id, EcrCode.Create(outputCode), "!Mass * CST.EF * !Dens");
        emission.SetEvaluationOrder(2);
        db.MethodologyFormulas.AddRange(mass, emission);
        var output = new MethodologyOutput(mainVersion.Id, EcrCode.Create(outputCode), unitT, 1);
        db.MethodologyOutputs.Add(output);

        // Одна речовина (числовий код поля SUBSTANCE_CODE, унікальний на тест): EF — за речовиною.
        var substanceCode = Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture);
        var registry = new RegistryDef(EcrCode.Create("MSXR" + tag), Name("substances"), false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var entry = new RegistryEntry(registry.Id, EcrCode.Create("E" + substanceCode), Name(substanceCode));
        var codeField = new RegistryFieldDef(registry.Id, EcrCode.Create("SUBSTANCE_CODE"), Name("code"), CellDataType.String, 1);
        db.RegistryEntries.Add(entry);
        db.RegistryFieldDefs.Add(codeField);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var codeValue = new RegistryValue(entry.Id, codeField.Id);
        codeValue.Set(CellDataType.String, substanceCode, null);
        db.RegistryValues.Add(codeValue);
        db.MethodologySubstances.Add(new MethodologySubstance(mainVersion.Id, entry.Id, 1));
        var ef = new MethodologyConstant(mainVersion.Id, EcrCode.Create("EF"), 0.5m, unitKg);
        ef.SetScope(null, entry.Id);
        db.MethodologyConstants.Add(ef);
        db.MethodologyRules.Add(new MethodologyRule(
            mainVersion.Id, EcrCode.Create("R_NORTH"), $"{{\"{columns[0].Id}\":\"North\"}}", 1));
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.CalculationBindings.Add(new CalculationBinding(table.Id, calculated.Id, main.Id, outputCode, "{}"));
        await db.SaveChangesAsync().ConfigureAwait(false);

        libraryVersion.Publish(1, "seed", new DateOnly(2099, 1, 1), true, Now);
        mainVersion.Publish(1, "seed", new DateOnly(2099, 1, 1), true, Now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(
            builder, document, periodKey, table.Code, outputCode, library.Id, libraryVersion.Id, libraryCode,
            main.Id, mainVersion.Id, mainCode, rowKeys);
    }

    private static void AddConstant(EcrDbContext db, int versionId, string code, decimal value, int unitId)
        => db.MethodologyConstants.Add(new MethodologyConstant(versionId, EcrCode.Create(code), value, unitId));

    private async Task<Users> SignInAllAsync(EcrApiFactory app, Stand s)
    {
        var publisher = await SignInAsync(app, ["Calculation.View", "Calculation.Publish"], s, grant: null).ConfigureAwait(false);
        var viewer = await SignInAsync(app, ["Calculation.View"], s, grant: null).ConfigureAwait(false);
        var worker = await SignInAsync(app, ["Document.View", "Calculation.View"], s, GrantLevel.Write).ConfigureAwait(false);
        return new Users(publisher, viewer, worker);
    }

    private async Task<HttpClient> SignInAsync(EcrApiFactory app, string[] permissions, Stand s, GrantLevel? grant)
    {
        var name = $"mig_{Guid.NewGuid():N}"[..20];
        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12].ToUpperInvariant()), Name("Script migration"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);
            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            if (grant is { } level)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Document.ProjectId, level));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {name}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Converted(string Text, string Fingerprint);

    /// <summary>Справжній перетворювач інструмента: текст версії й відбиток її джерела.</summary>
    private async Task<Converted> ConvertAsync(int versionId)
    {
        await using var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: true);
        var loaded = await new VersionSourceLoader(db, MigrationCli.CreateFormulaEngine()).LoadAsync(versionId, default).ConfigureAwait(false);
        Assert.False(loaded.HasErrors, string.Join("; ", loaded.Issues.Select(i => $"{i.Code}: {i.Message}")));
        var result = ScriptConverter.Convert(loaded.Source);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues.Select(i => $"{i.Code}: {i.Message}")));
        return new Converted(result.Text!, Fingerprint.ComputeHex(loaded.Source));
    }

    private async Task<(string? Main, string? Library)> ShadowAsync(Stand s)
    {
        await using var db = Context();
        var versions = await db.MethodologyVersions.AsNoTracking()
            .Where(v => v.Id == s.MainVersionId || v.Id == s.LibraryVersionId).ToListAsync().ConfigureAwait(false);
        return (versions.Single(v => v.Id == s.MainVersionId).ScriptText, versions.Single(v => v.Id == s.LibraryVersionId).ScriptText);
    }

    private async Task<MethodologyEngine[]> EnginesAsync(Stand s)
    {
        await using var db = Context();
        var versions = await db.MethodologyVersions.AsNoTracking()
            .Where(v => v.Id == s.MainVersionId || v.Id == s.LibraryVersionId).ToListAsync().ConfigureAwait(false);
        return [versions.Single(v => v.Id == s.MainVersionId).Engine, versions.Single(v => v.Id == s.LibraryVersionId).Engine];
    }

    private async Task<int> AuditCountAsync(int versionId, string reason)
    {
        await using var db = Context();
        return await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM aud.PublicationEvent WHERE EntityType = N'calc.MethodologyVersion' AND EntityId = {versionId} AND ChangeReason = {reason}")
            .SingleAsync().ConfigureAwait(false);
    }

    private static string[] Statuses(JsonElement response)
        => [.. response.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("status").GetString()!)];

    private static async Task<JsonElement> PostAsync(
        EcrApiFactory app, HttpClient client, string url, object body, HttpStatusCode expected)
    {
        using var response = await client.PostAsJsonAsync(new Uri(url, UriKind.Relative), body).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} замість {(int)expected}: {text}\n{app.ErrorsText}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task RecalculateAsync(EcrApiFactory app, HttpClient client, Stand s)
    {
        var accepted = await PostAsync(
            app, client, $"/api/v1/documents/{s.Document.DocumentId}/recalculate",
            new { periodKey = s.PeriodKey.ToString(CultureInfo.InvariantCulture) }, HttpStatusCode.Accepted).ConfigureAwait(false);
        var jobId = accepted.GetProperty("jobId").GetString()!;

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(false);
            var job = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
            var state = job.GetProperty("state").GetString();
            if (state is not ("Queued" or "Running"))
            {
                Assert.True(state == "Succeeded", $"Задача {jobId}: {job}\n{app.ErrorsText}");
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Задача {jobId} не завершилась: {job}\n{app.ErrorsText}");
            await Task.Delay(150).ConfigureAwait(false);
        }
    }

    /// <summary>Актуальні результати: <c>R{номер рядка}|значення</c>, за номером.</summary>
    private static async Task<List<string>> ResultsAsync(HttpClient client, Stand s)
    {
        using var response = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Document.DocumentId}/calculation-results?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(false);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
        return
        [
            .. body.EnumerateArray()
                .Select(r =>
                {
                    var value = r.GetProperty("value");
                    var number = value.ValueKind == JsonValueKind.String
                        ? decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture)
                        : value.GetDecimal();
                    return (
                        Code: r.GetProperty("outputCode").GetString()!,
                        Row: s.RowKeys.ToList().IndexOf(r.GetProperty("sourceRowKey").GetString()!) + 1,
                        Value: number);
                })
                .OrderBy(x => x.Code, StringComparer.Ordinal)
                .ThenBy(x => x.Row)
                .Select(x => $"{x.Code}|R{x.Row}|{x.Value:0.####}"),
        ];
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
