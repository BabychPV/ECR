// tests/Ecr.Application.Tests/Validation/RegFieldValidationRuleTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Validation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Validation;

/// <summary>
/// <c>REGFIELD</c> у правилах валідації (D16-04): контекст правила отримує
/// знімок полів довідника тим самим завантажувачем, що й перерахунок формул.
/// </summary>
/// <remarks>
/// ⛔ Доти обидва контексти правил (<c>SingleCellContext</c>, <c>ScopeContext</c>)
/// брали порожній знімок: <c>REGFIELD</c> давав <c>#REF</c>, правило
/// деградувало у Warning <c>ECR-VAL-RULE</c>, і <c>Error</c>-правило нічого
/// не блокувало. На шляху «Перевірити»/подання — ще й <c>#VALUE</c>: Lookup
/// приходив як <c>long</c>, а <c>ScopeContext.FromObject</c> гілки <c>long</c>
/// не мав і робив із нього <c>Text</c>.
///
/// Мутаційний доказ (прогнано руками, відкат `git checkout` файла):
/// не передати знімок у <c>SingleCellContext</c> — червоніє
/// <see cref="Коміркове_правило_з_REGFIELD_рахує_поле_довідника"/>(0);
/// у <c>ScopeContext</c> — <see cref="Правило_рядка_з_REGFIELD_над_long_Lookup"/>(0);
/// прибрати гілку <c>long</c> — <see cref="Lookup_як_long_у_правилі_рядка_це_число_а_не_текст"/>
/// і той самий <see cref="Правило_рядка_з_REGFIELD_над_long_Lookup"/>(0).
/// </remarks>
public sealed class RegFieldValidationRuleTests
{
    private const int RegistryDefId = 900;
    private const long EntryId = 5001;

    private static readonly IReadOnlyDictionary<string, ExpressionValue> NoHeaders =
        new Dictionary<string, ExpressionValue>();

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    private static ValidationEngine Engine() => new(new RealFormulaEngine());

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.8")]
    [Trait("Finding", "D16-04")]
    public async Task Коміркове_правило_з_REGFIELD_рахує_поле_довідника(int limit)
    {
        var (snapshot, table, permit) = Template(scope: 0);
        RegistryTestData.PermitLimit(_registries, RegistryDefId, EntryId, limit);
        var engine = Engine();

        // Знімок — тими самими публічними кроками, якими його будує
        // `TableValidation.LoadRegistryFieldsAsync`: що читають правила
        // (справжній DependencyExtractor) → спільний завантажувач.
        var reads = engine.RegistryFieldReads(table, snapshot);
        Assert.Equal([(permit.Id, "Limit")], reads);

        var fields = await RegistryFieldSnapshotLoader.LoadAsync(
            _registries, reads.Select(r => new RegistryFieldRequest(EntryId, RegistryDefId, r.FieldCode)),
            CancellationToken.None);

        var messages = engine.ValidateCell(
            permit, new CellValueData { ValueRegistryEntryId = EntryId }, table.ValidationRules,
            NoHeaders, "en", fields);

        Assert.DoesNotContain(messages, m => m.RuleCode == ValidationEngine.BrokenRuleCode);

        if (limit == 0)
        {
            // ⛔ Порушення — Error САМОГО правила, що блокує запис, а не
            // Warning «правило зламане» (#REF), який не блокує нічого.
            var message = Assert.Single(messages);
            Assert.Equal("PERMIT", message.RuleCode);
            Assert.Equal(ValidationSeverity.Error, message.Severity);
            Assert.True(message.BlocksSave);
        }
        else
        {
            Assert.Empty(messages);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-5.1")]
    [Trait("Finding", "D16-04")]
    public async Task Правило_рядка_з_REGFIELD_над_long_Lookup(int limit)
    {
        // Рівно те, що дає `TableValidation.SliceContext` на «Перевірити» і
        // поданні: Lookup-значення — `long` (`CellValueMapping.ToRuleValue`).
        var (snapshot, table, _) = Template(scope: 1);
        RegistryTestData.PermitLimit(_registries, RegistryDefId, EntryId, limit);
        var engine = Engine();

        var fields = await RegistryFieldSnapshotLoader.LoadAsync(
            _registries,
            engine.RegistryFieldReads(table, snapshot)
                .Select(r => new RegistryFieldRequest(EntryId, RegistryDefId, r.FieldCode)),
            CancellationToken.None);

        var messages = engine.ValidateScope(
            scope: 1, table.ValidationRules, new Values { ["Permit"] = EntryId }, NoHeaders, "en", fields);

        Assert.DoesNotContain(messages, m => m.RuleCode == ValidationEngine.BrokenRuleCode);

        if (limit == 0)
        {
            var message = Assert.Single(messages);
            Assert.Equal("PERMIT", message.RuleCode);
            Assert.Equal(ValidationSeverity.Error, message.Severity);
        }
        else
        {
            Assert.Empty(messages);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "D16-04")]
    public void Lookup_як_long_у_правилі_рядка_це_число_а_не_текст()
    {
        // ⛔ `ScopeContext.FromObject` без гілки `long` робив `Text("5001")`,
        // і рівність із числом 5001 ніколи не справджувалась.
        var rule = new ValidationRule(
            tableDefId: 3, EcrCode.Create("IS_PERMIT"), ValidationSeverity.Error, scope: 1,
            "[Permit] = 5001", new LocalizedText(new Dictionary<string, string> { ["en"] = "wrong permit" }));

        var messages = Engine().ValidateScope(
            scope: 1, [rule], new Values { ["Permit"] = EntryId }, NoHeaders, "en");

        Assert.Empty(messages);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "D16-04")]
    public void Правила_без_REGFIELD_не_просять_довідника()
    {
        // ⚠ Храповик звернень WR-04 не зсувається: таблиця без REGFIELD —
        // порожній список, тобто нуль звернень до IRegistryStore.
        var (snapshot, table, _) = Template(scope: 1, expression: "[Volume] >= 0");

        Assert.Empty(Engine().RegistryFieldReads(table, snapshot));
    }

    /// <summary>
    /// Таблиця з колонками <c>Volume</c> і Lookup <c>Permit</c> і правилом
    /// <c>REGFIELD([Permit], 'Limit') &gt; 0</c> рівня <paramref name="scope"/>.
    /// </summary>
    private static (TemplateVersionSnapshot Snapshot, TableDef Table, ColumnDef Permit) Template(
        byte scope, string expression = "REGFIELD([Permit], 'Limit') > 0")
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("Water"), "Main");
        builder.Column(table, "Volume");
        var permit = builder.Column(table, "Permit", CellDataType.Lookup);
        permit.SetLookup(RegistryDefId);
        builder.Row(table, "7001001", 1);

        table.AddValidationRule(new ValidationRule(
            table.Id, EcrCode.Create("PERMIT"), ValidationSeverity.Error, scope, expression,
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Permit limit is zero" })));

        return (builder.Build(), table, permit);
    }

    private sealed class Values : Dictionary<string, object?>, IValidationContext
    {
        public object? GetCell(string columnCode) => TryGetValue(columnCode, out var value) ? value : null;

        public object? GetCell(string rowKey, string columnCode) => GetCell(columnCode);
    }
}
