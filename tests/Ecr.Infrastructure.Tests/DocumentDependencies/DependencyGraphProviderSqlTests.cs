// tests/Ecr.Infrastructure.Tests/DocumentDependencies/DependencyGraphProviderSqlTests.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.DocumentDependencies;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
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
    private async Task MethodologyAsync(int registryId, TestDocument target)
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
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
