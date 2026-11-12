using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// ✎ R-3 (аудит 2026-11, рішення PO 11.11, D-478): <see cref="PublishChecks.CheckColumnTypeFamilies"/> — код колонки не
/// переходить в іншу родину типу зберігання між опублікованими/застарілими версіями шаблону (стабільна вʼюха SSRS).
/// Підключення в обробник доводить <c>PublishColumnTypeFamilyTests</c> на справжній базі.
/// </summary>
/// <remarks>
/// Мутації: порівнювати тип замість родини — тест «у межах родини» червоний; перевіряти «усі родини збігаються»
/// замість «хоча б одна» — тест наявного конфлікту червоний; порівнювати коди з урахуванням регістру — тест регістру
/// червоний.
/// </remarks>
public sealed class PublishColumnTypeFamilyCheckTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Код_колонки_що_змінив_родину_типу_дає_зауваження_з_ключем_і_підстановками()
    {
        var version = Draft(("X", CellDataType.Decimal));

        var diagnostic = Assert.Single(PublishChecks.CheckColumnTypeFamilies(
            version, [Published("X", CellDataType.String, "1.0.0.0")]));

        Assert.Equal("ECR-TMPL-0422", diagnostic.Code);
        Assert.Equal("err.ECR-TMPL-0422.columnTypeFamilyChanged", diagnostic.MessageKey);
        Assert.Equal("S.T.X", diagnostic.MessageParams!["columnPath"]);
        Assert.Equal("String (1.0.0.0)", diagnostic.MessageParams["publishedTypes"]);
        Assert.Equal("Decimal", diagnostic.MessageParams["newType"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData(CellDataType.Decimal, CellDataType.Int)]
    [InlineData(CellDataType.Formula, CellDataType.Decimal)]
    [InlineData(CellDataType.Calculated, CellDataType.Formula)]
    [InlineData(CellDataType.Lookup, CellDataType.Lookup)]
    public void Зміна_в_межах_родини_не_дає_зауваження(CellDataType before, CellDataType now)
        => Assert.Empty(PublishChecks.CheckColumnTypeFamilies(Draft(("X", now)), [Published("X", before, "1.0.0.0")]));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData(CellDataType.Lookup, CellDataType.String)]
    [InlineData(CellDataType.Decimal, CellDataType.Bool)]
    [InlineData(CellDataType.Date, CellDataType.Decimal)]
    [InlineData(CellDataType.Unit, CellDataType.Int)]
    public void Зміна_родини_дає_зауваження(CellDataType before, CellDataType now)
        => Assert.Single(PublishChecks.CheckColumnTypeFamilies(Draft(("X", now)), [Published("X", before, "1.0.0.0")]));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Наявний_конфлікт_не_блокує_версію_з_наявною_родиною_але_блокує_нову()
    {
        // Дані до D-478: v1 — String, v2 — Decimal (конфлікт уже в базі).
        IReadOnlyList<PublishedColumnType> published =
        [
            Published("X", CellDataType.String, "1.0.0.0"),
            Published("X", CellDataType.Decimal, "2.0.0.0"),
        ];

        Assert.Empty(PublishChecks.CheckColumnTypeFamilies(Draft(("X", CellDataType.Decimal)), published));
        Assert.Empty(PublishChecks.CheckColumnTypeFamilies(Draft(("X", CellDataType.String)), published));

        var diagnostic = Assert.Single(PublishChecks.CheckColumnTypeFamilies(Draft(("X", CellDataType.Date)), published));
        Assert.Equal("String (1.0.0.0), Decimal (2.0.0.0)", diagnostic.MessageParams!["publishedTypes"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Коди_порівнюються_без_регістру_а_нові_коди_й_інші_таблиці_не_рахуються()
    {
        var version = Draft(("x", CellDataType.Decimal), ("NEW", CellDataType.Date));

        Assert.Single(PublishChecks.CheckColumnTypeFamilies(version, [Published("X", CellDataType.String, "1.0.0.0")]));
        Assert.Empty(PublishChecks.CheckColumnTypeFamilies(
            version, [new PublishedColumnType("S", "OTHER", "X", CellDataType.String, "1.0.0.0")]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Видалена_колонка_чернетки_не_перевіряється()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var table = builder.Table(builder.Sheet("S"), "T");
        var column = builder.Column(table, "X", CellDataType.Decimal);
        var version = builder.Version();
        TemplateBuilder.Delete(column);

        Assert.Empty(PublishChecks.CheckColumnTypeFamilies(version, [Published("X", CellDataType.String, "1.0.0.0")]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Родина_типу_як_у_стабільній_вʼюсі()
    {
        // Сторож синхронності з SQL — StableViewTypeFamilyTests (Ecr.Architecture.Tests).
        var families = Enum.GetValues<CellDataType>().ToDictionary(t => t, PublishChecks.StorageFamily);

        Assert.Equal(0, families[CellDataType.String]);
        Assert.Equal(1, families[CellDataType.Int]);
        Assert.Equal(1, families[CellDataType.Decimal]);
        Assert.Equal(3, families[CellDataType.Bool]);
        Assert.Equal(4, families[CellDataType.Date]);
        Assert.Equal(5, families[CellDataType.Lookup]);
        Assert.Equal(1, families[CellDataType.Formula]);
        Assert.Equal(7, families[CellDataType.Unit]);
        Assert.Equal(1, families[CellDataType.Calculated]);
    }

    private static TemplateVersion Draft(params (string Code, CellDataType Type)[] columns)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var table = builder.Table(builder.Sheet("S"), "T");
        foreach (var (code, type) in columns)
        {
            builder.Column(table, code, type);
        }

        return builder.Version();
    }

    private static PublishedColumnType Published(string column, CellDataType type, string version)
        => new("S", "T", column, type, version);
}
