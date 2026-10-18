// tests/Ecr.Domain.Tests/Configuration/PermitPersistenceTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Інваріанти сутностей дозволів у домені (Permits, PM-02). Ті самі правила,
/// що тримають CHECK-обмеження міграції <c>PRM01_Permits</c>, — для вставок повз домен.
/// </summary>
public sealed class PermitPersistenceTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("")]
    [InlineData("1ABC")]
    [InlineData("A-B")]
    [InlineData("A B")]
    public void Код_типу_дозволу_має_формат_EcrCode(string code)
    {
        // Код проходить через EcrCode.Create — той самий вираз, що в решти сутностей конфігурації.
        Assert.ThrowsAny<Exception>(() => NewType(code));
    }

    [Fact]
    public void Новий_тип_дозволу_є_чернеткою_першої_версії_без_публікації()
    {
        var type = NewType("AIR_PERMIT");

        Assert.Equal(("AIR_PERMIT", PermitTypeStatus.Draft, 1, PermitCategory.Air), (type.Code, type.Status, type.Version, type.Category));
        Assert.Null(type.PublishedAt);
        Assert.Null(type.PublishedByUserId);
    }

    [Fact]
    public void Публікація_ставить_стан_момент_і_автора()
    {
        var type = NewType("AIR_PERMIT");

        type.Publish(7, Now);

        Assert.Equal((PermitTypeStatus.Published, (DateTime?)Now, (int?)7), (type.Status, type.PublishedAt, type.PublishedByUserId));
    }

    [Fact]
    public void Перехід_Draft_Published_лише_вгору_повторна_публікація_відхиляється()
    {
        var type = NewType("AIR_PERMIT");
        type.Publish(7, Now);

        Assert.Throws<InvalidOperationException>(() => type.Publish(8, Now.AddHours(1)));

        // Автор і момент першої публікації не перезаписані невдалою спробою.
        Assert.Equal((7, Now), (type.PublishedByUserId, type.PublishedAt));
    }

    [Fact]
    public void Опублікований_тип_не_редагується()
    {
        var type = NewType("AIR_PERMIT");
        type.Publish(7, Now);

        Assert.Throws<InvalidOperationException>(() => type.Update(Name("New"), PermitCategory.Water));
        Assert.Equal(PermitCategory.Air, type.Category);
    }

    [Fact]
    public void Чернетка_редагується_а_момент_публікації_має_бути_в_UTC()
    {
        var type = NewType("AIR_PERMIT");

        type.Update(Name("New"), PermitCategory.Waste);
        Assert.Equal(PermitCategory.Waste, type.Category);

        Assert.Throws<ArgumentException>(() => type.Publish(7, new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Local)));
        Assert.Equal(PermitTypeStatus.Draft, type.Status);
    }

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData(PermitListScope.SharedRegistry, 5, null, true)]
    [InlineData(PermitListScope.TypeOnly, 5, null, true)]
    [InlineData(PermitListScope.SamePermitTable, null, "SOURCES", true)]
    [InlineData(PermitListScope.SharedRegistry, null, null, false)]
    [InlineData(PermitListScope.TypeOnly, null, "SOURCES", false)]
    [InlineData(PermitListScope.SamePermitTable, null, null, false)]
    [InlineData(PermitListScope.SamePermitTable, 5, "SOURCES", false)]
    [InlineData(PermitListScope.SharedRegistry, 5, "SOURCES", false)]
    [InlineData(null, 5, null, false)]
    [InlineData(null, null, "SOURCES", false)]
    public void Область_переліку_узгоджена_з_посиланнями(
        PermitListScope? scope, int? registryDefId, string? sibling, bool expected)
    {
        Assert.Equal(expected, PermitFieldMeta.IsListConsistent(scope, registryDefId, sibling));

        var create = () => new PermitFieldMeta(
            10, 1, null, null, null, scope, registryDefId, sibling is null ? null : EcrCode.Create(sibling));

        if (expected)
        {
            Assert.Equal(scope, create().ListScope);
        }
        else
        {
            Assert.Throws<ArgumentException>(create);
        }
    }

    [Fact]
    public void Масштаб_не_перевищує_точність_і_не_буває_без_неї()
    {
        Assert.Throws<ArgumentException>(() => new PermitFieldMeta(10, 1, null, 5, 6, null, null, null));
        Assert.Throws<ArgumentException>(() => new PermitFieldMeta(10, 1, null, null, 2, null, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PermitFieldMeta(10, 1, null, 39, 2, null, null, null));

        var ok = new PermitFieldMeta(10, 1, EcrCode.Create("LIMITS"), 18, 6, null, null, null);
        Assert.Equal(("LIMITS", (byte?)18, (byte?)6), (ok.GroupCode, ok.Precision, ok.Scale));
    }

    [Fact]
    public void Прив_язка_за_шапкою_потребує_поля_шапки_а_інша_його_забороняє()
    {
        Assert.Throws<ArgumentException>(() => NewBinding(PermitSelectionRule.FromHeaderLookup, null));
        Assert.Throws<ArgumentException>(() => NewBinding(PermitSelectionRule.ByFacilityPeriodEnd, 4));

        Assert.Equal(4, NewBinding(PermitSelectionRule.FromHeaderLookup, 4).HeaderFieldDefId);
        Assert.Null(NewBinding(PermitSelectionRule.ByFacilityPeriodEnd, null).HeaderFieldDefId);
    }

    [Fact]
    public void Колонки_прив_язки_не_повторюють_ні_поле_дозволу_ні_колонку_аркуша()
    {
        var binding = NewBinding(PermitSelectionRule.ByFacilityPeriodEnd, null);
        binding.AddColumn(new PermitSheetBindingColumn(PermitColumnRole.RowKey, EcrCode.Create("SOURCE"), 100, 1));
        binding.AddColumn(new PermitSheetBindingColumn(PermitColumnRole.Limit, EcrCode.Create("LIMIT"), 101, 2));

        Assert.Throws<InvalidOperationException>(() => binding.AddColumn(
            new PermitSheetBindingColumn(PermitColumnRole.Info, EcrCode.Create("SOURCE"), 102, 3)));
        Assert.Throws<InvalidOperationException>(() => binding.AddColumn(
            new PermitSheetBindingColumn(PermitColumnRole.Info, EcrCode.Create("NOTE"), 100, 3)));

        Assert.Equal(["SOURCE", "LIMIT"], binding.Columns.Select(c => c.PermitFieldCode));
    }

    [Fact]
    public void Таблиця_типу_зберігає_код_реєстр_і_позицію_а_оновлення_міняє_лише_назву_й_позицію()
    {
        var table = new PermitTypeTable(1, EcrCode.Create("SOURCES"), Name("Sources"), 1, 9);

        table.Update(Name("Emission sources"), 3);

        Assert.Equal(("SOURCES", 9, 3, "Emission sources"), (table.Code, table.RegistryDefId, table.Ordinal, table.NameL10n.Values["en"]));
    }

    private static PermitTypeDef NewType(string code)
        => new(EcrCode.Create(code), Name("Air permit"), PermitCategory.Air, 3);

    private static PermitSheetBinding NewBinding(PermitSelectionRule rule, int? headerFieldDefId)
        => new(EcrCode.Create("B1"), 1, 2, 3, 4, PermitBindingMode.Rows, rule, headerFieldDefId);

    private static LocalizedText Name(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
