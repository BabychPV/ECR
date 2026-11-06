using Ecr.Application.Documents.FormulaOverride;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ P3 06.11 (Аудит-2): тип результату формули для ручного значення (D-461) — з виразу, а не з наявного значення
/// комірки. Порожня комірка числової формули приймала текст, бо значення типу не казало.
/// </summary>
/// <remarks>
/// Мутація (прогнано): <c>OfAsync</c> завжди <c>Unknown</c> — червоніють усі числові/текстові випадки (і HTTP «порожня
/// комірка числової формули — 422»).
/// </remarks>
public sealed class FormulaResultKindTests
{
    private const int RegistryDefId = 910;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("[A] * 2", FormulaResultKind.Number)]
    [InlineData("SUM([A], [B])", FormulaResultKind.Number)]
    [InlineData("[A] > 3", FormulaResultKind.Number)]
    [InlineData("REGFIELD([Poll], 'COMPONENT')", FormulaResultKind.Text)]
    [InlineData("REGFIELD([Poll], 'LIMIT')", FormulaResultKind.Number)]
    [InlineData("IF([A] > 3, 'high', 'low')", FormulaResultKind.Text)]
    [InlineData("IF([A] > 3, [A] * 2, 0)", FormulaResultKind.Number)]
    [InlineData("IFERROR(REGFIELD([Poll], 'LIMIT'), 0)", FormulaResultKind.Number)]
    [InlineData("[Name]", FormulaResultKind.Text)]
    [InlineData("[Other]", FormulaResultKind.Unknown)]
    [InlineData("REGFIELD([Poll], 'NOPE')", FormulaResultKind.Unknown)]
    public async Task Тип_результату_береться_з_виразу(string expression, FormulaResultKind expected)
    {
        var (table, formula) = Arrange(expression);

        var kind = await FormulaResultKinds.OfAsync(formula, table, _registries, CancellationToken.None);

        Assert.Equal(expected, kind);
    }

    private (TableDef Table, FormulaDef Formula) Arrange(string expression)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var table = builder.Table(builder.Sheet("Air"), "Main");
        builder.Column(table, "A", CellDataType.Decimal);
        builder.Column(table, "B", CellDataType.Decimal);
        builder.Column(table, "Name", CellDataType.String);
        builder.Column(table, "Other", CellDataType.Formula);
        builder.Column(table, "Poll", CellDataType.Lookup).SetLookup(RegistryDefId);
        var target = builder.Column(table, "Out", CellDataType.Formula);
        var formula = builder.Formula(table, expression, column: target);

        var registry = new RegistryDef(
            EcrCode.Create("SUBST"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Substances" }), isTemporal: false);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(registry, RegistryDefId);
        AddField(registry, "COMPONENT", CellDataType.String, 1);
        AddField(registry, "LIMIT", CellDataType.Decimal, 2);
        _registries.FindDefinitionByIdAsync(RegistryDefId, Arg.Any<CancellationToken>()).Returns(registry);

        return (table, formula);
    }

    private static void AddField(RegistryDef registry, string code, CellDataType type, int id)
    {
        var field = new RegistryFieldDef(
            RegistryDefId, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), type, ordinal: id);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(field, id);
        registry.AddField(field);
    }
}
