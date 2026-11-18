using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Авторська сторона скрипта методології на реальному SQL Server (MS T3.1): повні заміни виходів, речовин, імпортів,
/// пошук речовин за кодом, «живі» таблиці за кодом (колонки й псевдоніми місяців), ребра залежностей.
/// </summary>
[Collection("SqlServer")]
public sealed class MethodologyScriptAuthoringStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = value });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Виходи_замінюються_повністю_незмінні_рядки_лишаються_змінені_перестворюються()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];
        int versionId;
        int unitA;
        int unitB;

        await using (var seed = builder.CreateContext())
        {
            var method = new Methodology(EcrCode.Create($"MSO_{tag}"), Name("outputs"));
            seed.Methodologies.Add(method);
            await seed.SaveChangesAsync();
            var version = new MethodologyVersion(
                method.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
            seed.MethodologyVersions.Add(version);
            await seed.SaveChangesAsync();
            versionId = version.Id;
            var units = await seed.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).Take(2).ToListAsync();
            unitA = units[0];
            unitB = units[1];
        }

        await using (var write = builder.CreateContext())
        {
            await new MethodologyStore(write).ReplaceScriptOutputsAsync(
                versionId,
                [new ScriptOutputRow("OUT_A", unitA, 1, false), new ScriptOutputRow("OUT_B", unitA, 2, true)],
                default);
            await write.SaveChangesAsync();
        }

        int keptId;
        await using (var read = builder.CreateContext())
        {
            keptId = (await read.MethodologyOutputs.AsNoTracking()
                .SingleAsync(o => o.MethodologyVersionId == versionId && o.Code == "OUT_A")).Id;
        }

        await using (var write = builder.CreateContext())
        {
            // OUT_A не змінився, OUT_B змінив одиницю й порядок, OUT_C новий.
            await new MethodologyStore(write).ReplaceScriptOutputsAsync(
                versionId,
                [
                    new ScriptOutputRow("OUT_A", unitA, 1, false),
                    new ScriptOutputRow("OUT_B", unitB, 3, true),
                    new ScriptOutputRow("out_c", unitA, 2, false),
                    new ScriptOutputRow("OUT_C", unitB, 9, true),
                ],
                default);
            await write.SaveChangesAsync();
        }

        await using (var read = builder.CreateContext())
        {
            var rows = await read.MethodologyOutputs.AsNoTracking()
                .Where(o => o.MethodologyVersionId == versionId).OrderBy(o => o.Ordinal).ToListAsync();
            Assert.Equal(["OUT_A", "out_c", "OUT_B"], rows.Select(r => r.Code));
            Assert.Equal(keptId, rows.Single(r => r.Code == "OUT_A").Id);
            Assert.Equal(unitB, rows.Single(r => r.Code == "OUT_B").UnitId);
            Assert.True(rows.Single(r => r.Code == "OUT_B").IsPerSubstance);
            Assert.False(rows.Single(r => r.Code == "out_c").IsPerSubstance);
        }

        await using (var write = builder.CreateContext())
        {
            await new MethodologyStore(write).ReplaceScriptOutputsAsync(versionId, [], default);
            await write.SaveChangesAsync();
        }

        await using var empty = builder.CreateContext();
        Assert.Empty(await empty.MethodologyOutputs.AsNoTracking().Where(o => o.MethodologyVersionId == versionId).ToListAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Речовини_й_імпорти_замінюються_повністю_порядок_за_переліком()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];
        int versionId;
        int libA;
        int libB;
        long entry1;
        long entry2;
        long entry3;

        await using (var seed = builder.CreateContext())
        {
            var method = new Methodology(EcrCode.Create($"MSS_{tag}"), Name("subst"));
            var a = new Methodology(EcrCode.Create($"MSLA_{tag}"), Name("lib a"));
            var b = new Methodology(EcrCode.Create($"MSLB_{tag}"), Name("lib b"));
            a.SetKind(MethodologyKind.Library);
            b.SetKind(MethodologyKind.Library);
            seed.Methodologies.AddRange(method, a, b);
            await seed.SaveChangesAsync();
            var version = new MethodologyVersion(
                method.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
            seed.MethodologyVersions.Add(version);

            var registry = new RegistryDef(EcrCode.Create($"MSR_{tag}"), Name("reg"), isTemporal: false);
            seed.RegistryDefs.Add(registry);
            await seed.SaveChangesAsync();
            var e1 = new RegistryEntry(registry.Id, EcrCode.Create($"MA{tag}"), Name("1"));
            var e2 = new RegistryEntry(registry.Id, EcrCode.Create($"MB{tag}"), Name("2"));
            var e3 = new RegistryEntry(registry.Id, EcrCode.Create($"MC{tag}"), Name("3"));
            seed.RegistryEntries.AddRange(e1, e2, e3);
            await seed.SaveChangesAsync();

            versionId = version.Id;
            libA = a.Id;
            libB = b.Id;
            entry1 = e1.Id;
            entry2 = e2.Id;
            entry3 = e3.Id;
        }

        await using (var write = builder.CreateContext())
        {
            var store = new MethodologyStore(write);
            await store.ReplaceScriptSubstancesAsync(versionId, [entry1, entry2, entry1], default);
            await store.ReplaceScriptImportsAsync(versionId, [libA, libA], default);
            await write.SaveChangesAsync();
        }

        await using (var write = builder.CreateContext())
        {
            var store = new MethodologyStore(write);

            // Порядок змінився й додалась третя: ordinal — позиція в переліку.
            await store.ReplaceScriptSubstancesAsync(versionId, [entry3, entry2], default);
            await store.ReplaceScriptImportsAsync(versionId, [libB], default);
            await write.SaveChangesAsync();
        }

        await using var read = builder.CreateContext();
        var substances = await read.MethodologySubstances.AsNoTracking()
            .Where(s => s.MethodologyVersionId == versionId).OrderBy(s => s.Ordinal).ToListAsync();
        Assert.Equal([entry3, entry2], substances.Select(s => s.SubstanceEntryId));
        Assert.Equal([1, 2], substances.Select(s => s.Ordinal));

        var imports = await read.MethodologyImports.AsNoTracking()
            .Where(i => i.MethodologyVersionId == versionId).Select(i => i.ImportedMethodologyId).ToListAsync();
        Assert.Equal([libB], imports);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Речовини_шукаються_за_полем_SUBSTANCE_CODE_а_без_поля_за_кодом_запису_лише_в_довідниках_із_полем()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];
        long withValue;
        long withoutValue;

        await using (var seed = builder.CreateContext())
        {
            var withField = new RegistryDef(EcrCode.Create($"MSF_{tag}"), Name("with field"), isTemporal: false);
            var plain = new RegistryDef(EcrCode.Create($"MSN_{tag}"), Name("plain"), isTemporal: false);
            seed.RegistryDefs.AddRange(withField, plain);
            await seed.SaveChangesAsync();

            var field = new RegistryFieldDef(
                withField.Id, EcrCode.Create("SUBSTANCE_CODE"), Name("code"), CellDataType.String, 1);
            seed.RegistryFieldDefs.Add(field);
            await seed.SaveChangesAsync();

            var e1 = new RegistryEntry(withField.Id, EcrCode.Create($"SA{tag}"), Name("a"));
            var e2 = new RegistryEntry(withField.Id, EcrCode.Create($"SB{tag}"), Name("b"));
            var e3 = new RegistryEntry(plain.Id, EcrCode.Create($"SP{tag}"), Name("p"));
            seed.RegistryEntries.AddRange(e1, e2, e3);
            await seed.SaveChangesAsync();

            var value = new RegistryValue(e1.Id, field.Id);
            value.Set(CellDataType.String, $"X{tag}", unitId: null);
            seed.RegistryValues.Add(value);
            await seed.SaveChangesAsync();

            withValue = e1.Id;
            withoutValue = e2.Id;
        }

        await using var read = builder.CreateContext();
        var found = await new MethodologyStore(read).FindSubstanceEntryIdsAsync(
            [$"X{tag}", $"SB{tag}", $"SP{tag}", $"NOPE{tag}", "  "], default);

        Assert.Equal(withValue, found[$"X{tag}"]);
        Assert.Equal(withoutValue, found[$"SB{tag}"]);
        Assert.False(found.ContainsKey($"SP{tag}"));
        Assert.False(found.ContainsKey($"NOPE{tag}"));
        Assert.Empty(await new MethodologyStore(read).FindSubstanceEntryIdsAsync([], default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Жива_таблиця_за_кодом_віддає_колонки_і_псевдоніми_місяців_лише_останньої_опублікованої_версії()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 209901, columnCount: 2, monthColumns: 2);
        int templateId;
        string templateCode;
        string tableCode;
        int calculatedId;

        await using (var seed = builder.CreateContext())
        {
            var table = await seed.TableDefs.AsNoTracking().SingleAsync(t => t.Id == doc.TableDefId);
            tableCode = table.Code;
            var template = await seed.Templates.SingleAsync(t => t.Id == doc.TemplateId);
            templateId = template.Id;
            templateCode = template.Code;

            // Колонка-результат і видалена колонка (її у відповіді бути не має).
            var calc = new ColumnDef(doc.TableDefId, EcrCode.Create($"CALC_{templateId}"), Name("calc"), 20, CellDataType.Calculated);
            var gone = new ColumnDef(doc.TableDefId, EcrCode.Create($"GONE_{templateId}"), Name("gone"), 21, CellDataType.Decimal);
            seed.ColumnDefs.AddRange(calc, gone);
            await seed.SaveChangesAsync();
            calculatedId = calc.Id;
            gone.SoftDelete(1, Now);

            seed.MonthInputAliases.Add(new MonthInputAlias(doc.TableDefId, "Hours", 1, doc.MonthColumnDefIds[0]));
            seed.MonthInputAliases.Add(new MonthInputAlias(doc.TableDefId, "Hours", 2, doc.MonthColumnDefIds[1]));

            var version = await seed.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId);
            version.Publish(1, Now);
            await seed.SaveChangesAsync();
        }

        await using var read = builder.CreateContext();
        var store = new MethodologyStore(read);

        var live = await store.FindLiveTablesByCodeAsync(tableCode, null, default);
        var one = Assert.Single(live);
        Assert.Equal(doc.TableDefId, one.TableDefId);
        Assert.Equal(doc.TemplateVersionId, one.TemplateVersionId);
        Assert.Equal(templateCode, one.TemplateCode);
        Assert.Equal(["Hours"], one.MonthAliases);
        Assert.DoesNotContain(one.Columns, c => c.Code.StartsWith("GONE_", StringComparison.Ordinal));

        var calculated = Assert.Single(one.Columns, c => c.DataType == CellDataType.Calculated);
        Assert.Equal(calculatedId, calculated.Id);

        var month = Assert.Single(one.Columns, c => c.IsMonthColumn && c.MonthNumber == 2);
        Assert.Equal(doc.MonthColumnDefIds[1], month.Id);

        Assert.Single(await store.FindLiveTablesByCodeAsync(tableCode, templateCode, default));
        Assert.Empty(await store.FindLiveTablesByCodeAsync(tableCode, "NO_SUCH_TEMPLATE", default));
        Assert.Empty(await store.FindLiveTablesByCodeAsync("NO_SUCH_TABLE_" + tableCode, null, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_шаблону_не_є_живою_таблицею()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 209902);

        await using var read = builder.CreateContext();
        var tableCode = (await read.TableDefs.AsNoTracking().SingleAsync(t => t.Id == doc.TableDefId)).Code;

        Assert.Empty(await new MethodologyStore(read).FindLiveTablesByCodeAsync(tableCode, null, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ребра_залежностей_читаються_по_методологіях()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];
        int a;
        int b;
        int c;

        await using (var seed = builder.CreateContext())
        {
            var ma = new Methodology(EcrCode.Create($"MDA_{tag}"), Name("a"));
            var mb = new Methodology(EcrCode.Create($"MDB_{tag}"), Name("b"));
            var mc = new Methodology(EcrCode.Create($"MDC_{tag}"), Name("c"));
            seed.Methodologies.AddRange(ma, mb, mc);
            await seed.SaveChangesAsync();
            a = ma.Id;
            b = mb.Id;
            c = mc.Id;
            seed.MethodologyDependencies.Add(new MethodologyDependency(a, b));
            seed.MethodologyDependencies.Add(new MethodologyDependency(a, c));
            seed.MethodologyDependencies.Add(new MethodologyDependency(b, c));
            await seed.SaveChangesAsync();
        }

        await using var read = builder.CreateContext();
        var edges = await new MethodologyStore(read).GetMethodologyDependenciesAsync([a, b, c], default);

        Assert.Equal([b, c], edges[a].Order());
        Assert.Equal([c], edges[b]);
        Assert.False(edges.ContainsKey(c));
        Assert.Empty(await new MethodologyStore(read).GetMethodologyDependenciesAsync([], default));
    }

    /// <summary>
    /// Мутаційний доказ (F13): два редактори, що прочитали той самий хеш, не затирають один одного. Без
    /// <c>IsConcurrencyToken</c> на <c>ScriptHash</c> друге збереження мовчки перезаписувало перше, а звірка
    /// <c>expectedHash</c> в обробнику (читання, потім запис) цього не ловила.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Два_редактори_з_одним_прочитаним_хешем_другий_отримує_конфлікт_а_не_затирає_перший(bool hadScript)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];
        int versionId;

        await using (var seed = builder.CreateContext())
        {
            var method = new Methodology(EcrCode.Create($"MSC_{tag}"), Name("concurrency"));
            seed.Methodologies.Add(method);
            await seed.SaveChangesAsync();
            var version = new MethodologyVersion(
                method.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
            if (hadScript)
            {
                version.SetScript("// v0", [1]);
            }

            seed.MethodologyVersions.Add(version);
            await seed.SaveChangesAsync();
            versionId = version.Id;
        }

        await using var first = builder.CreateContext();
        await using var second = builder.CreateContext();
        var firstVersion = await first.MethodologyVersions.SingleAsync(v => v.Id == versionId);
        var secondVersion = await second.MethodologyVersions.SingleAsync(v => v.Id == versionId);

        firstVersion.SetScript("// A", [10]);
        await first.SaveChangesAsync();

        secondVersion.SetScript("// B", [20]);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var read = builder.CreateContext();
        var stored = await read.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == versionId);
        Assert.Equal("// A", stored.ScriptText);
        Assert.Equal([10], stored.ScriptHash);
    }

    private async Task<int> SeedDraftScriptAsync(TestDocumentBuilder builder, string code)
    {
        await using var seed = builder.CreateContext();
        var method = new Methodology(EcrCode.Create(code), Name("publish race"));
        seed.Methodologies.Add(method);
        await seed.SaveChangesAsync();
        var version = new MethodologyVersion(
            method.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
        version.SetScript("// v0", [1]);
        seed.MethodologyVersions.Add(version);
        await seed.SaveChangesAsync();
        return version.Id;
    }

    /// <summary>
    /// F13a, гонка «Save проти Publish», бік збереження: публікація пише <c>Status</c>, але не <c>ScriptHash</c>, тож без
    /// <c>Status</c> у токені паралельності збереження, що прочитало ще чернетку, проходило ПІСЛЯ коміту публікації й
    /// міняло текст опублікованої (вже матеріалізованої) версії.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збереження_що_прочитало_чернетку_після_публікації_отримує_конфлікт_і_текст_опублікованої_не_міняється()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var versionId = await SeedDraftScriptAsync(builder, $"MSP_{Guid.NewGuid().ToString("N")[..8]}");

        await using var publisher = builder.CreateContext();
        await using var saver = builder.CreateContext();
        var publishing = await publisher.MethodologyVersions.SingleAsync(v => v.Id == versionId);
        var saving = await saver.MethodologyVersions.SingleAsync(v => v.Id == versionId);

        publishing.Publish(1, "race", new DateOnly(2099, 3, 1), testsPassed: true, Now);
        await publisher.SaveChangesAsync();

        saving.SetScript("// B", [20]);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => saver.SaveChangesAsync());

        await using var read = builder.CreateContext();
        var stored = await read.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == versionId);
        Assert.Equal(TemplateVersionStatus.Published, stored.Status);
        Assert.Equal("// v0", stored.ScriptText);
        Assert.Equal([1], stored.ScriptHash);
    }

    /// <summary>
    /// F13a, бік публікації: публікація, що працювала з уже застарілим текстом (збереження встигло), не публікує —
    /// версія лишається чернеткою з новим текстом.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_що_працювала_із_застарілим_текстом_отримує_конфлікт_і_версія_лишається_чернеткою()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var versionId = await SeedDraftScriptAsync(builder, $"MSQ_{Guid.NewGuid().ToString("N")[..8]}");

        await using var publisher = builder.CreateContext();
        await using var saver = builder.CreateContext();
        var publishing = await publisher.MethodologyVersions.SingleAsync(v => v.Id == versionId);
        var saving = await saver.MethodologyVersions.SingleAsync(v => v.Id == versionId);

        saving.SetScript("// B", [20]);
        await saver.SaveChangesAsync();

        publishing.Publish(1, "race", new DateOnly(2099, 3, 1), testsPassed: true, Now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => publisher.SaveChangesAsync());

        await using var read = builder.CreateContext();
        var stored = await read.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == versionId);
        Assert.Equal(TemplateVersionStatus.Draft, stored.Status);
        Assert.Equal("// B", stored.ScriptText);
    }
}
