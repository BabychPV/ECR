// tests/Ecr.Infrastructure.Tests/DocumentDependencies/DependencyGraphProviderSqlTests.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.Scripting;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.DocumentDependencies;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Ecr.Infrastructure.Tests.DocumentDependencies;

/// <summary>
/// B179 L1 на справжньому SQL: шаблон-джерело (як HSE400) → довідник за <c>cfg.DocumentSourceBinding</c> → шаблони-споживачі
/// (як Land) — формулою з ребрами публікації (<c>cfg.FormulaDependency</c> вид 2), формулою версії без ребер (AST) і
/// методологією (<c>cfg.RegistryUse</c> вид 1 + <c>cfg.CalculationBinding</c>). Жодного рядка
/// <c>cfg.DocumentSourceBindingConsumer</c>.
/// </summary>
/// <remarks>
/// ⚠ Періоди 2099xx; власні довідник, шаблони, зв'язка й методологія на тест. Ребра формул пише той самий видобувач, що й
/// публікація (<see cref="PublishChecks.Dependencies"/> → <c>ReplaceFormulaDependenciesAsync</c>).
/// Мутації (локально, B179 L1): не читати <c>cfg.FormulaDependency</c> → джерело доказу «ast» замість
/// «formulaDependency» (червоний); прибрати запасний AST → немає споживача B (червоний); прибрати читання методологій
/// адаптером → немає споживача C (червоний); обсяг завжди <c>All</c> → червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Directive", "B179")]
public sealed class DependencyGraphProviderSqlTests(SqlServerFixture sql)
{
    private const int PeriodKey = 209910;
    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private string Registry => $"HC{_tag}";

    [Fact]
    public async Task Джерело_доходить_до_споживачів_формулою_AST_і_методологією_з_обсягом_за_періодом()
    {
        var registryId = await NewRegistryAsync();
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await NewBindingAsync(source.TemplateId, registryId);

        var landRows = await ConsumerAsync($"REGFIELD(REGFIND('{Registry}', [Period].Year, [Period].Sequence, 'FG', 'Density'), 'VALUE')", extract: true);
        var landAst = await ConsumerAsync($"REGFIELD(REGFIND('{Registry}', [Period].Year, 10, 'FG', 'Density'), 'VALUE')", extract: false);
        var landMethodology = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await MethodologyAsync(registryId, landMethodology);

        var graph = await NewProvider(new MemoryCache(new MemoryCacheOptions())).GetAsync(CancellationToken.None);

        var consumers = graph.ConsumersOf(source.TemplateId).ToDictionary(d => d.ConsumerTemplateId);
        Assert.Equal(PeriodScope.Month, consumers[landRows.TemplateId].Scope);
        Assert.True(consumers[landRows.TemplateId].ViaTemplateFormula);
        Assert.Equal(PeriodScope.FixedMonth(10), consumers[landAst.TemplateId].Scope);
        Assert.True(consumers[landMethodology.TemplateId].ViaMethodology);
        Assert.False(consumers[landMethodology.TemplateId].ViaTemplateFormula);
        Assert.Equal(PeriodScope.Month, consumers[landMethodology.TemplateId].Scope);

        Assert.Equal("formulaDependency", Evidence(graph, registryId, landRows.TemplateId).Single().Source);
        Assert.Equal("ast", Evidence(graph, registryId, landAst.TemplateId).Single().Source);
        Assert.Contains(graph.Diagnostics, d => d.Key == "astFallback" && d.Detail == $"template version {landAst.TemplateVersionId}");
        Assert.DoesNotContain(graph.Diagnostics, d => d.Key == "astFallback" && d.Detail == $"template version {landRows.TemplateVersionId}");

        var levelOf = graph.Levels.SelectMany((level, i) => level.Select(t => (t, i))).ToDictionary(x => x.t, x => x.i);
        Assert.All(new[] { landRows.TemplateId, landAst.TemplateId, landMethodology.TemplateId }, t => Assert.True(levelOf[t] > levelOf[source.TemplateId]));
        Assert.DoesNotContain(graph.Cycles, c => c.Contains(source.TemplateId));
    }

    [Fact]
    public async Task Публікація_шаблону_змінює_ревізію_і_кеш_віддає_новий_граф()
    {
        var registryId = await NewRegistryAsync();
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await NewBindingAsync(source.TemplateId, registryId);
        var late = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);

        var memory = new MemoryCache(new MemoryCacheOptions());
        var first = await NewProvider(memory).GetAsync(CancellationToken.None);
        Assert.Same(first, await NewProvider(memory).GetAsync(CancellationToken.None));
        Assert.DoesNotContain(first.ConsumersOf(source.TemplateId), d => d.ConsumerTemplateId == late.TemplateId);

        await AddFormulaAsync(late, $"REGFIND('{Registry}', [Period].Year, [Period].Sequence, 'FG', 'X')", extract: true, publish: true);

        var second = await NewProvider(memory).GetAsync(CancellationToken.None);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Contains(second.ConsumersOf(source.TemplateId), d => d.ConsumerTemplateId == late.TemplateId);
    }

    /// <summary>
    /// MS x B179: методологія (скрипт чи формули), що читає РЕЗУЛЬТАТИ іншої методології й пише в ІНШИЙ шаблон, - споживач
    /// довідника, який читає джерело результатів; ланцюг транзитивний, а без ребра читання шаблон у графі не з'являється.
    /// </summary>
    /// <remarks>
    /// Мутації (локально): адаптер повертає порожній <c>ListResultReadsAsync</c> - червоний; провайдер не заповнює
    /// <c>MethodologyResultReads</c> - червоний.
    /// </remarks>
    [Fact]
    public async Task Читач_результатів_іншої_методології_є_споживачем_довідника_транзитивно_і_з_усіма_періодами()
    {
        var registryId = await NewRegistryAsync();
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await NewBindingAsync(source.TemplateId, registryId);
        var land = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        var landMethodology = await MethodologyAsync(registryId, land);
        var report = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        var (reportMethodology, _) = await ResultReaderAsync(landMethodology, report, "A");
        var total = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await ResultReaderAsync(reportMethodology, total, "B");
        var unrelated = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await ResultReaderAsync(null, unrelated, "C");

        var graph = await NewProvider(new MemoryCache(new MemoryCacheOptions())).GetAsync(CancellationToken.None);

        var consumers = graph.ConsumersOf(source.TemplateId).ToDictionary(d => d.ConsumerTemplateId);
        Assert.Equal(PeriodScope.Month, consumers[land.TemplateId].Scope);
        Assert.Equal(PeriodScope.All, consumers[report.TemplateId].Scope);
        Assert.True(consumers[report.TemplateId].ViaMethodology);
        Assert.Contains(total.TemplateId, consumers.Keys);
        Assert.DoesNotContain(unrelated.TemplateId, consumers.Keys);
        Assert.Contains(graph.Edges, e => e.Kind == DependencyEdgeKind.MethodologyResult
            && e.From == new DependencyNodeRef(DependencyNodeKind.Methodology, landMethodology)
            && e.To == new DependencyNodeRef(DependencyNodeKind.Methodology, reportMethodology));
    }

    /// <summary>
    /// MS x B179: перемикання рушія ОПУБЛІКОВАНОЇ версії (Formulas - Script) не чіпає статусу й <c>PublishedAt</c>, а
    /// переписує ребра довідників і залежності методологій - ревізія графа мусить це бачити, інакше кеш лишає старі ребра.
    /// </summary>
    /// <remarks>
    /// Мутації (локально, кожна по черзі прибирає свій складник ревізії в <c>MethodologyRegistryEdges.RevisionAsync</c>):
    /// рушії - червоний перший крок; ребра довідників - другий; залежності - третій.
    /// </remarks>
    [Fact]
    public async Task Ревізія_змінюється_при_перемиканні_рушія_ребрах_довідників_і_залежностях_методологій()
    {
        var registryId = await NewRegistryAsync();
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await NewBindingAsync(source.TemplateId, registryId);
        var land = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        var landMethodology = await MethodologyAsync(registryId, land);
        var report = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        var (reportMethodology, reportVersion) = await ResultReaderAsync(null, report, "A");

        var memory = new MemoryCache(new MemoryCacheOptions());
        var before = await MethodologyRevisionAsync();
        var firstGraph = await NewProvider(memory).GetAsync(CancellationToken.None);
        Assert.DoesNotContain(firstGraph.ConsumersOf(source.TemplateId), d => d.ConsumerTemplateId == report.TemplateId);

        // 1. Рушій версії - Script (як script-migration/switch): статус, PublishedAt і склад версій ті самі.
        await using (var db = sql.CreateContext())
        {
            await db.Database.ExecuteSqlAsync($"UPDATE calc.MethodologyVersion SET Engine = 1 WHERE Id = {reportVersion}");
        }

        var afterEngine = await MethodologyRevisionAsync();
        Assert.NotEqual(before, afterEngine);

        // 2. Скрипт версії читає довідник (ребро cfg.RegistryUse з кодом "script", як у ScriptMaterializer).
        await using (var db = sql.CreateContext())
        {
            db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(reportVersion, "script", registryId, fieldPath: null));
            await db.SaveChangesAsync();
        }

        var afterUses = await MethodologyRevisionAsync();
        Assert.NotEqual(afterEngine, afterUses);
        var secondGraph = await NewProvider(memory).GetAsync(CancellationToken.None);
        var scriptConsumer = Assert.Single(secondGraph.ConsumersOf(source.TemplateId), d => d.ConsumerTemplateId == report.TemplateId);
        Assert.Equal(PeriodScope.All, scriptConsumer.Scope);
        Assert.True(scriptConsumer.ViaMethodology);

        // 3. Залежність методологій (скрипт читає результати землі).
        await using (var db = sql.CreateContext())
        {
            db.MethodologyDependencies.Add(new MethodologyDependency(reportMethodology, landMethodology));
            await db.SaveChangesAsync();
        }

        var afterDependency = await MethodologyRevisionAsync();
        Assert.NotEqual(afterUses, afterDependency);
        Assert.Contains((await NewProvider(memory).GetAsync(CancellationToken.None)).Edges, e => e.Kind == DependencyEdgeKind.MethodologyResult);

        // Контроль: нічого не змінилось - ревізія та сама.
        Assert.Equal(afterDependency, await MethodologyRevisionAsync());
    }

    /// <summary>
    /// F13: <c>GetFreshAsync</c> обходить вікно мемоізації ревізії (<c>GetAsync</c> у вікні віддає старий граф, свіжий -
    /// новий). Тестовий постачальник решти набору має вікно 0, тому цей шлях без цього тесту не покритий.
    /// </summary>
    /// <remarks>Мутація: у <c>RevisionAsync</c> прибрати <c>!fresh &amp;&amp;</c> - тест червоний.</remarks>
    [Fact]
    public async Task GetFreshAsync_обходить_вікно_ревізії_а_GetAsync_у_вікні_віддає_старий_граф()
    {
        var registryId = await NewRegistryAsync();
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await NewBindingAsync(source.TemplateId, registryId);
        var late = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);

        var memory = new MemoryCache(new MemoryCacheOptions());
        await using var db = sql.CreateContext();
        var provider = WindowedProvider(db, memory);
        var first = await provider.GetAsync(CancellationToken.None);
        Assert.DoesNotContain(first.ConsumersOf(source.TemplateId), d => d.ConsumerTemplateId == late.TemplateId);

        await AddFormulaAsync(late, $"REGFIND('{Registry}', [Period].Year, [Period].Sequence, 'FG', 'X')", extract: true, publish: true);

        Assert.Same(first, await provider.GetAsync(CancellationToken.None));
        var fresh = await provider.GetFreshAsync(CancellationToken.None);
        Assert.Contains(fresh.ConsumersOf(source.TemplateId), d => d.ConsumerTemplateId == late.TemplateId);
    }

    /// <summary>
    /// F13: відмова циклу відкочує транзакцію публікації, але свіжа ревізія, прочитана ВСЕРЕДИНІ транзакції, не має
    /// потрапити в мемоізацію: інакше вікно ревізії віддає граф відкоченої (неіснуючої) зміни.
    /// </summary>
    /// <remarks>Мутація: у <c>RevisionAsync</c> писати ревізію у кеш і для <c>fresh</c> - тест червоний.</remarks>
    [Fact]
    public async Task Свіжа_ревізія_з_відкоченої_транзакції_не_отруює_вікно_мемоізації()
    {
        var registryId = await NewRegistryAsync();
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        var late = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await AddFormulaAsync(late, $"REGFIND('{Registry}', [Period].Year, [Period].Sequence, 'FG', 'X')", extract: true, publish: true);

        var memory = new MemoryCache(new MemoryCacheOptions());
        await using var db = sql.CreateContext();
        var provider = WindowedProvider(db, memory);
        var before = await provider.GetAsync(CancellationToken.None);
        Assert.DoesNotContain(before.ConsumersOf(source.TemplateId), d => d.ConsumerTemplateId == late.TemplateId);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.DocumentSourceBindings.Add(new DocumentSourceBinding($"B179R_{_tag}", source.TemplateId, registryId));
            await db.SaveChangesAsync();
            var inside = await provider.GetFreshAsync(CancellationToken.None);
            Assert.Contains(inside.ConsumersOf(source.TemplateId), d => d.ConsumerTemplateId == late.TemplateId);
            await tx.RollbackAsync();
        }

        var after = await provider.GetAsync(CancellationToken.None);
        Assert.DoesNotContain(after.ConsumersOf(source.TemplateId), d => d.ConsumerTemplateId == late.TemplateId);
    }

    /// <summary>
    /// MS x B179: обсяг читання довідника ОПУБЛІКОВАНИМ скриптом береться з AST його тексту (а не «усе»): місяць споживача =
    /// місяць джерела, літеральний місяць, а невпевнений ключ (без <c>period.*</c>) лишається <c>All</c>.
    /// </summary>
    /// <remarks>
    /// Мутації (локально): у <c>DependencyGraphProvider</c> не читати <c>row.ScriptText</c> (скрипт - завжди <c>All</c>) -
    /// тест червоний; у <c>MethodologyRegistryEdges.ListReadsAsync</c> не заповнювати <c>ScriptText</c> - червоний.
    /// </remarks>
    [Fact]
    public async Task Скрипт_читає_довідник_з_обсягом_за_періодом_із_AST_а_невпевнений_ключ_дає_All()
    {
        var registryId = await NewRegistryAsync();
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await NewBindingAsync(source.TemplateId, registryId);

        var month = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        var fixedMonth = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        var unknown = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await ScriptMethodologyAsync(registryId, month, "M", "period.Year, period.Month");
        await ScriptMethodologyAsync(registryId, fixedMonth, "F", "period.Year, 10");
        await ScriptMethodologyAsync(registryId, unknown, "U", "\"x\"");

        var graph = await NewProvider(new MemoryCache(new MemoryCacheOptions())).GetAsync(CancellationToken.None);

        var consumers = graph.ConsumersOf(source.TemplateId).ToDictionary(d => d.ConsumerTemplateId);
        Assert.True(consumers[month.TemplateId].ViaMethodology);
        Assert.Equal(PeriodScope.Month, consumers[month.TemplateId].Scope);
        Assert.Equal(PeriodScope.FixedMonth(10), consumers[fixedMonth.TemplateId].Scope);
        Assert.Equal(PeriodScope.All, consumers[unknown.TemplateId].Scope);
    }

    /// <summary>Опублікований скрипт читає довідник ключем <paramref name="keys"/> і пише в колонку шаблону документа.</summary>
    private async Task ScriptMethodologyAsync(int registryId, TestDocument target, string suffix, string keys)
    {
        await using var db = sql.CreateContext();
        var methodology = new Methodology(EcrCode.Create($"S{suffix}{_tag}"), Text("s"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
        var text = "rows from T1;\n"
            + $"output OUT_A : kg = REGFIELD(REGFIND(\"{Registry}\", {keys}, \"a\"), \"VALUE\");";
        version.SetScript(text, ScriptCompiler.Hash(text));
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();
        version.Publish(1, "B179", new DateOnly(2099, 1, 1), testsPassed: true, Now);
        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version.Id, "script", registryId, fieldPath: null));
        db.CalculationBindings.Add(new CalculationBinding(target.TableDefId, target.ColumnDefIds[2], methodology.Id, "OUT_A", "{}"));
        await db.SaveChangesAsync();
    }

    private static DependencyGraphProvider WindowedProvider(EcrDbContext db, IMemoryCache memory)
        => new(
            db, memory, new RealFormulaEngine(), new MethodologyRegistryEdges(db),
            new CacheLifetimes(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1)));

    private static IReadOnlyList<DependencyEvidence> Evidence(DependencyGraphSnapshot graph, int registryId, int templateId)
        => graph.Edges.Single(e => e.Kind == DependencyEdgeKind.TemplateFormula
                                   && e.From == new DependencyNodeRef(DependencyNodeKind.Registry, registryId)
                                   && e.To == new DependencyNodeRef(DependencyNodeKind.Template, templateId)).Evidence;

    private DependencyGraphProvider NewProvider(IMemoryCache memory)
    {
        var db = sql.CreateContext();
        return new DependencyGraphProvider(
            db, memory, new RealFormulaEngine(), new MethodologyRegistryEdges(db),
            new CacheLifetimes(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), TimeSpan.Zero));
    }

    private async Task<int> NewRegistryAsync()
    {
        await using var db = sql.CreateContext();
        var registry = new RegistryDef(EcrCode.Create(Registry), Text("Composition"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry.Id;
    }

    private async Task NewBindingAsync(int sourceTemplateId, int registryId)
    {
        await using var db = sql.CreateContext();
        db.DocumentSourceBindings.Add(new DocumentSourceBinding($"B179_{_tag}", sourceTemplateId, registryId));
        await db.SaveChangesAsync();
    }

    private async Task<TestDocument> ConsumerAsync(string expression, bool extract)
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: PeriodKey);
        await AddFormulaAsync(document, expression, extract, publish: false);
        return document;
    }

    /// <summary>Формула колонки; <paramref name="extract"/> — ребра пише видобувач публікації.</summary>
    private async Task AddFormulaAsync(TestDocument document, string expression, bool extract, bool publish)
    {
        await using var db = sql.CreateContext();
        var formula = new FormulaDef(document.TableDefId, FormulaScope.Column, expression, ExpressionDialect.Template);
        formula.AssignColumn(document.ColumnDefIds[1]);
        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync();

        var store = new TemplateVersionStore(db);
        var version = await store.GetWithStructureAsync(document.TemplateVersionId, CancellationToken.None);
        if (extract)
        {
            var dependencies = PublishChecks.Dependencies(version, new RealFormulaEngine());
            Assert.Contains(dependencies, d => d.DependsOnKind == DependencyExtractor.KindRegistry && d.TableDefId is null && d.RowKey == Registry);
            await store.ReplaceFormulaDependenciesAsync(document.TemplateVersionId, dependencies, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        if (publish)
        {
            version.Publish(1, Now);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Опублікована методологія читає довідник за місяцем і пише в колонку шаблону документа.</summary>
    private async Task<int> MethodologyAsync(int registryId, TestDocument target)
    {
        await using var db = sql.CreateContext();
        var methodology = new Methodology(EcrCode.Create($"M{_tag}"), Text("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();
        db.MethodologyFormulas.Add(version.AddFormula(
            EcrCode.Create("F1"),
            $"REGFIELD(REGFIND('{Registry}', [Period].Year, [Period].Sequence, @Stream, 'Density'), 'VALUE')",
            FormulaResultType.Number,
            null));
        version.Publish(1, "B179", new DateOnly(2099, 1, 1), testsPassed: true, Now);
        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version.Id, "F1", registryId, "VALUE"));
        db.CalculationBindings.Add(new CalculationBinding(target.TableDefId, target.ColumnDefIds[2], methodology.Id, "tons", "{}"));
        await db.SaveChangesAsync();
        return methodology.Id;
    }

    /// <summary>
    /// Опублікована методологія БЕЗ читання довідників, що пише в колонку шаблону документа; <paramref name="sourceMethodologyId"/>
    /// — методологія, чиї результати вона читає (<c>calc.MethodologyDependency</c>: скрипт чи формула). Повертає її версію.
    /// </summary>
    private async Task<(int MethodologyId, int VersionId)> ResultReaderAsync(int? sourceMethodologyId, TestDocument target, string suffix)
    {
        await using var db = sql.CreateContext();
        var methodology = new Methodology(EcrCode.Create($"R{suffix}{_tag}"), Text("r"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();
        version.Publish(1, "B179", new DateOnly(2099, 1, 1), testsPassed: true, Now);
        db.CalculationBindings.Add(new CalculationBinding(target.TableDefId, target.ColumnDefIds[2], methodology.Id, "tons", "{}"));
        if (sourceMethodologyId is { } source)
        {
            db.MethodologyDependencies.Add(new MethodologyDependency(methodology.Id, source));
        }

        await db.SaveChangesAsync();
        return (methodology.Id, version.Id);
    }

    private async Task<string> MethodologyRevisionAsync()
    {
        await using var db = sql.CreateContext();
        return await new MethodologyRegistryEdges(db).RevisionAsync(CancellationToken.None);
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
