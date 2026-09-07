using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Попередній перегляд мапінгу на реальних рядках джерела (<c>ФВ-13.14</c>).
/// </summary>
/// <remarks>
/// ⛔ Перевіряється не «екран щось показав», а те, заради чого перегляд
/// існує: **розриви**. Мапінг, який зійшовся, у перегляд не дивиться ніхто —
/// у нього заглядають тоді, коли числа не ті. Тому тести йдуть по трьох
/// розривах окремо: поле джерела в нікуди, мапінг без жодного реального
/// рядка, колонка без нічого за нею.
///
/// ⚠ Уся логіка перевіряється через <see cref="PreviewMappingHandler.Compose"/> —
/// без бази і без HTTP. Якби класифікацію розривів робив SQL, кожен із цих
/// тестів вимагав би живої схеми, і писали б їх рівно стільки, скільки
/// зазвичай пишуть інтеграційних: жодного.
/// </remarks>
public sealed class MappingPreviewTests
{
    private static readonly DateTime From = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-13.14")]
    public void Перегляд_показує_реальні_рядки_джерела_і_число_яке_ляже_в_комірку()
    {
        // ⛔ Дослівна вимога: «мапінг має попередній перегляд НА РЕАЛЬНИХ
        // РЯДКАХ джерела». Тому перевіряється і те, що рядки джерела видно як
        // є, і те, що поруч із ними стоїть адреса та число, яке за цим
        // мапінгом опиниться в комірці.
        var data = Data(
            maps: [Map(1, "Flare_01_CO", rowKey: "Flare_01", aggregation: "Sum")],
            points:
            [
                Point("Flare_01_CO", From.AddHours(1), 10m),
                Point("Flare_01_CO", From.AddHours(2), 32.5m),
            ]);

        var preview = PreviewMappingHandler.Compose(data, From, To);

        Assert.Equal(2, preview.PointsSeen);

        // Реальні рядки — з їхніми власними значеннями і мітками часу.
        Assert.Collection(
            preview.Rows,
            row =>
            {
                Assert.Equal("Flare_01_CO", row.SourcePath);
                Assert.Equal(From.AddHours(1), row.Timestamp);
                Assert.Equal(10m, row.ValueNumeric);
                Assert.Equal("Flare_01", row.TargetRowKey);
                Assert.Equal("CO_MASS", row.TargetColumnCode);
                Assert.Equal(MappingOutcome.Materialized, row.Outcome);
            },
            row => Assert.Equal(32.5m, row.ValueNumeric));

        // ⚠ І число: перегляд без нього показував би зв'язок, а не результат.
        var field = Assert.Single(preview.Fields);
        Assert.Equal(42.5m, field.FoldedValue);
        Assert.Equal(2, field.PointCount);
        Assert.Equal(MappingOutcome.Materialized, field.Outcome);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Поле_джерела_яке_не_лягає_нікуди_названо_окремо()
    {
        // ⛔ Перший розрив. Тег збирається, точки лежать у базі — і жоден
        // мапінг їх не бере. Без цього переліку такий тег невідрізнимий від
        // тега, який лягає в комірку: обидва «успішно зібралися».
        var data = Data(
            maps: [Map(1, "Flare_01_CO", rowKey: "Flare_01", aggregation: "Sum")],
            points:
            [
                Point("Flare_01_CO", From.AddHours(1), 10m),
                Point("Flare_01_NOx", From.AddHours(1), 3m),
                Point("Flare_01_NOx", From.AddHours(2), 4m),
            ]);

        var preview = PreviewMappingHandler.Compose(data, From, To);

        var orphan = Assert.Single(preview.UnmappedSourceFields);
        Assert.Equal("Flare_01_NOx", orphan.SourcePath);
        Assert.Equal(2, orphan.PointCount);
        Assert.Equal(From.AddHours(2), orphan.LastSeenUtc);

        Assert.Contains(preview.Rows, r => r.Outcome == MappingOutcome.Unmapped
                                           && r.TargetColumnCode is null);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Мапінг_під_який_немає_жодного_рядка_названо_NoData()
    {
        // ⛔ Другий розрив, і найдорожчий: це друкарська помилка в шляху AF.
        // Сьогодні її знаходять через місяць порожнім збором — прогін
        // «успішний», точок нуль, і в переліку прогонів він виглядає як
        // справний (ІНТ-3.3).
        var data = Data(
            maps:
            [
                Map(1, "Flare_01_CO", rowKey: "Flare_01", aggregation: "Sum"),
                Map(2, "Flare_1_CO", rowKey: "Flare_01", aggregation: "Sum", columnCode: "CO_ALT"),
            ],
            points: [Point("Flare_01_CO", From.AddHours(1), 10m)]);

        var preview = PreviewMappingHandler.Compose(data, From, To);

        var typo = Assert.Single(preview.Fields, f => f.SourceField == "Flare_1_CO");
        Assert.Equal(MappingOutcome.NoData, typo.Outcome);
        Assert.Equal(0, typo.PointCount);
        Assert.Null(typo.FoldedValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Колонка_за_якою_не_стоїть_нічого_названа_розривом()
    {
        // ⛔ Третій розрив — з боку документа. Колонка існує, у звіті вона є, і
        // заповнити її нічим.
        var data = Data(
            maps: [Map(1, "Flare_01_CO", rowKey: "Flare_01", aggregation: "Sum")],
            points: [Point("Flare_01_CO", From.AddHours(1), 10m)],
            columns:
            [
                Column(100, "CO_MASS", hasFieldMap: true),
                Column(101, "CH4_MASS"),
            ]);

        var preview = PreviewMappingHandler.Compose(data, From, To);

        var hole = Assert.Single(preview.UncoveredColumns);
        Assert.Equal("CH4_MASS", hole.Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void За_колонкою_може_стояти_методологія_або_формула_а_не_лише_мапінг()
    {
        // ⛔ Перевірка лише по мапінгах оголосила б розривом КОЖНУ обчислювану
        // колонку — і перелік розривів перестали б читати після третього
        // хибного рядка. Значення методології не копіюється в комірку, воно
        // читається за посиланням (`D-69`), тому «порожня комірка» тут не
        // ознака.
        var data = Data(
            maps: [Map(1, "Flare_01_CO", rowKey: "Flare_01", aggregation: "Sum")],
            points: [Point("Flare_01_CO", From.AddHours(1), 10m)],
            columns:
            [
                Column(101, "CO2_TONS", hasBinding: true),
                Column(102, "TOTAL", hasFormula: true),
                Column(103, "NOTHING"),
            ]);

        var preview = PreviewMappingHandler.Compose(data, From, To);

        var hole = Assert.Single(preview.UncoveredColumns);
        Assert.Equal("NOTHING", hole.Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Колонку_яку_не_заповнить_ніхто_відрізнено_від_колонки_для_ручного_вводу()
    {
        // ⚠ «Заповнюється людиною» — законна відповідь на питання «чому за
        // колонкою нічого не стоїть». А колонка, у яку не можна ані ввести
        // руками, ані порахувати, не отримає значення НІКОЛИ. Злити ці два
        // стани в один означало б знецінити другий.
        var data = Data(
            maps: [Map(1, "Flare_01_CO", rowKey: "Flare_01", aggregation: "Sum")],
            points: [Point("Flare_01_CO", From.AddHours(1), 10m)],
            columns:
            [
                Column(101, "MANUAL"),
                Column(102, "LOCKED", isReadOnly: true),
            ]);

        var preview = PreviewMappingHandler.Compose(data, From, To);

        Assert.True(Assert.Single(preview.UncoveredColumns, c => c.Code == "LOCKED").IsUnfillable);
        Assert.False(Assert.Single(preview.UncoveredColumns, c => c.Code == "MANUAL").IsUnfillable);

        // ⚠ Невиправне стоїть попереду: перелік читають згори.
        Assert.Equal("LOCKED", preview.UncoveredColumns[0].Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Мапінг_на_видалену_колонку_названо_TargetMissing()
    {
        // ⚠ Колонку прибрали в шаблоні, мапінг лишився. Перенос про такий
        // мапінг мовчить — він бере лише ті, у яких колонка є.
        var data = Data(
            maps: [Map(1, "Flare_01_CO", rowKey: "Flare_01", aggregation: "Sum", columnCode: null)],
            points: [Point("Flare_01_CO", From.AddHours(1), 10m)]);

        var preview = PreviewMappingHandler.Compose(data, From, To);

        Assert.Equal(MappingOutcome.TargetMissing, Assert.Single(preview.Fields).Outcome);
        Assert.Equal(MappingOutcome.TargetMissing, Assert.Single(preview.Rows).Outcome);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Мапінг_без_рядка_адресата_це_RawOnly_а_не_дефект()
    {
        // ⚠ `TargetRowKey = null` означає рівно одне: точки лишаються сирими
        // для звірки (`D-118`). Це законний стан, і назвати його розривом
        // означало б показувати помилку там, де її свідомо обрали.
        var data = Data(
            maps: [Map(1, "Flare_01_CO", rowKey: null, aggregation: null)],
            points: [Point("Flare_01_CO", From.AddHours(1), 10m)]);

        var preview = PreviewMappingHandler.Compose(data, From, To);

        var field = Assert.Single(preview.Fields);
        Assert.Equal(MappingOutcome.RawOnly, field.Outcome);
        Assert.Null(field.FoldedValue);
        Assert.Empty(preview.UnmappedSourceFields);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Урізана_вибірка_позначається_прапорцем()
    {
        // ⛔ Урізана серія дає правильне НА ВИГЛЯД число: `Sum` просто менша,
        // `Last` просто інша. Мовчазне урізання зробило б перегляд брехливим
        // саме там, де на нього дивляться.
        var data = Data(
            maps: [Map(1, "Flare_01_CO", rowKey: "Flare_01", aggregation: "Sum")],
            points: [Point("Flare_01_CO", From.AddHours(1), 10m)],
            isTruncated: true);

        Assert.True(PreviewMappingHandler.Compose(data, From, To).IsTruncated);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Перевернуте_вікно_відхиляється_а_не_виправляється()
    {
        // ⛔ Обмін меж дав би правдоподібний перегляд ЗОВСІМ іншого проміжку:
        // той, хто надіслав запит, помилився в одному з двох полів, і мовчазна
        // перестановка приховала б помилку разом із її наслідком.
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(1, To, From, default));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_права_Integration_Manage_перегляд_недоступний()
    {
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Registry.View").Build());

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(To);

        var handler = new PreviewMappingHandler(
            Substitute.For<IMappingPreviewStore>(), access, user, clock);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(1, From, To, default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Невідома_сутність_джерела_це_404_а_не_порожній_перегляд()
    {
        // ⚠ Порожній перегляд неіснуючої сутності читався б як «мапінгів
        // немає» — тобто як справний стан налаштованої інтеграції.
        await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(404, From, To, default));
    }

    /// <summary>Обробник із правом і зі сховищем, яке нічого не знає.</summary>
    private static PreviewMappingHandler Handler()
    {
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Integration.Manage").Build());

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(To);

        return new PreviewMappingHandler(
            Substitute.For<IMappingPreviewStore>(), access, user, clock);
    }

    /// <summary>Матеріал перегляду.</summary>
    private static MappingPreviewData Data(
        IReadOnlyList<FieldMapRef> maps,
        IReadOnlyList<RawPointRef> points,
        IReadOnlyList<TargetColumnRef>? columns = null,
        bool isTruncated = false)
        => new(new SourceEntityRef(1, "FLARE_01", "Факел 01"), maps, points, isTruncated, columns ?? []);

    /// <summary>Мапінг поля.</summary>
    private static FieldMapRef Map(
        int id, string field, string? rowKey, string? aggregation, string? columnCode = "CO_MASS")
        => new(id, field, rowKey, 100, columnCode, columnCode is not null, aggregation, "kg", "t");

    /// <summary>Реальна точка джерела.</summary>
    private static RawPointRef Point(string path, DateTime at, decimal value)
        => new(path, at, value, null, "Good");

    /// <summary>Колонка цільової таблиці.</summary>
    private static TargetColumnRef Column(
        int id,
        string code,
        bool isReadOnly = false,
        bool hasFieldMap = false,
        bool hasBinding = false,
        bool hasFormula = false)
        => new(50, id, code, code, isReadOnly, false, false, hasFieldMap, hasBinding, hasFormula);
}
