// tests/Ecr.Domain.Tests/Configuration/RegistryCompositionTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Композиція, режим коду і мітка зміни даних у домені (RT-03,
/// FEATURE-REGISTRY-TABLES §3.3, §4.8; <c>D-155</c>, <c>D-157</c>, <c>D-163</c>).
/// </summary>
/// <remarks>
/// Ті самі інваріанти в базі перевіряє <c>RegistryCompositionSchemaTests</c>
/// (Ecr.Infrastructure.Tests): домен ловить помилку коду раніше, база — вставку
/// повз домен.
/// </remarks>
public sealed class RegistryCompositionTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Directive", "RT-03")]
    [Trait("Requirement", "ФВ-8.3")]
    public void Нове_поле_посилання_а_не_композиція_з_забороною_видалення()
    {
        var field = Field(CellDataType.Lookup);

        Assert.Equal(RegistryRelationKind.Reference, field.RelationKind);
        Assert.Equal(ParentDeletePolicy.Restrict, field.OnParentDelete);
    }

    [Theory]
    [Trait("Directive", "RT-03")]
    [InlineData(ParentDeletePolicy.Restrict)]
    [InlineData(ParentDeletePolicy.Cascade)]
    public void Поле_Lookup_стає_композицією_з_заданою_політикою(ParentDeletePolicy policy)
    {
        var field = Field(CellDataType.Lookup);

        field.ComposeInto(policy);

        Assert.Equal(RegistryRelationKind.Composition, field.RelationKind);
        Assert.Equal(policy, field.OnParentDelete);
    }

    [Theory]
    [Trait("Directive", "RT-03")]
    [InlineData(CellDataType.String)]
    [InlineData(CellDataType.Int)]
    [InlineData(CellDataType.Decimal)]
    [InlineData(CellDataType.Date)]
    [InlineData(CellDataType.Unit)]
    public void Композиція_на_полі_не_Lookup_відхиляється_і_поле_лишається_посиланням(CellDataType type)
    {
        var field = Field(type);

        Assert.Throws<InvalidOperationException>(() => field.ComposeInto(ParentDeletePolicy.Cascade));

        Assert.Equal(RegistryRelationKind.Reference, field.RelationKind);
        Assert.Equal(ParentDeletePolicy.Restrict, field.OnParentDelete);
    }

    [Fact]
    [Trait("Directive", "RT-03")]
    [Trait("Requirement", "ФВ-8.2")]
    public void Новий_довідник_має_ручний_код_і_не_має_мітки_зміни()
    {
        var registry = Registry();

        Assert.Equal(RegistryCodeMode.Manual, registry.CodeMode);
        Assert.Null(registry.DataChangedAt);

        registry.UseCodeMode(RegistryCodeMode.Auto);
        Assert.Equal(RegistryCodeMode.Auto, registry.CodeMode);
    }

    [Fact]
    [Trait("Directive", "RT-03")]
    public void Мітка_зміни_даних_приймає_лише_UTC()
    {
        var registry = Registry();

        Assert.Throws<ArgumentException>(
            () => registry.MarkDataChanged(DateTime.SpecifyKind(Now, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(
            () => registry.MarkDataChanged(DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)));
        Assert.Null(registry.DataChangedAt);

        registry.MarkDataChanged(Now);
        Assert.Equal(Now, registry.DataChangedAt);
    }

    private static RegistryFieldDef Field(CellDataType type)
        => new(1, EcrCode.Create("CASE"), Text("Case"), type, 1);

    private static RegistryDef Registry()
        => new(EcrCode.Create("GAS_COMPOSITION"), Text("Gas composition"), isTemporal: false);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
