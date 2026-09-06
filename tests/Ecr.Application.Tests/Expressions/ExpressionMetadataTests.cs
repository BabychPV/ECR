using System.Reflection;
using Ecr.Application.Common;
using Ecr.Application.Expressions;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Expressions;

/// <summary>
/// Склад мови для редактора: функції діалекту і символи контексту
/// (<c>ФВ-9.15a</c> — автодоповнення <c>CST.</c>, <c>!</c>, <c>@</c>, <c>HDR.</c>).
/// </summary>
/// <remarks>
/// ⛔ Склад мови віддає сервер, а не зашитий перелік у клієнті. Ці тести
/// стережуть саме це: додавання функції на сервері має з'явитися в редакторі
/// без жодної правки клієнта, інакше нова функція виглядатиме в ньому як
/// помилка користувача.
/// </remarks>
public sealed class ExpressionMetadataTests
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IRepository<TemplateVersion, int> _versions =
        Substitute.For<IRepository<TemplateVersion, int>>();

    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IUnitCatalog _catalogue = Substitute.For<IUnitCatalog>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ExpressionMetadataTests()
    {
        _catalogue.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        _methodologies.GetSymbolsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                      .Returns(new MethodologySymbols([], [], []));

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p",
            UserId = 9,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(StringComparer.Ordinal)
            {
                "Calculation.View", "Template.View",
            },
            Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Діалект_шаблону_дає_рівно_свої_дванадцять_функцій()
    {
        var result = await Handler()
            .HandleAsync(ExpressionDialect.Template, null, null, CancellationToken.None)
            .ConfigureAwait(true);

        // ⚠ Набір закритий (`02b` §7): розширення — зміна контракту. Число тут
        // не «поточне», а домовлене, і його зміна мусить бути помічена.
        Assert.Equal(12, result.Functions.Count);
        Assert.Contains(result.Functions, f => f.Name == "CONVERT");

        // ⛔ `VLOOKUP` відсутній НАВМИСНО: усі 429 його входжень у чинному
        // шаблоні — звернення до довідників, замінені посиланням на реєстр.
        // Побачити його в підказці означало б запросити писати те, що
        // публікація відхилить.
        Assert.DoesNotContain(result.Functions, f => f.Name == "VLOOKUP");
        Assert.DoesNotContain(result.Functions, f => f.Name == "POWER");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Діалект_методологій_дає_виміряний_набір_а_не_вигаданий()
    {
        // ⛔ Тест переписаний за `Q-082`. Стояло «24 функції, серед них SUM» —
        // і обидва твердження були неправдою про чинну систему: замір
        // (`tests/Ecr.Legacy.Probe`) дає 22 ядра плюс 4 розширення, а `SUM` у
        // діалекті методологій немає ЗА ПОБУДОВОЮ МОВИ — там немає діапазонів,
        // операнди скалярні. Редактор пропонував агрегат, який нема до чого
        // застосувати.
        var result = await Handler()
            .HandleAsync(ExpressionDialect.Methodology, null, null, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(26, result.Functions.Count);
        Assert.Equal(22, result.Functions.Count(f => f.Tier == "Core"));

        Assert.Contains(result.Functions, f => f.Name == "SUBSTANCE");
        Assert.Contains(result.Functions, f => f.Name == "Pow");
        Assert.Contains(result.Functions, f => f.Name == "if");

        Assert.DoesNotContain(result.Functions, f => f.Name == "SUM");
        Assert.DoesNotContain(result.Functions, f => f.Name == "POWER");
        Assert.DoesNotContain(result.Functions, f => f.Name == "SWITCH");

        // ⚠ Регістр — частина імені: `ROUND` у чинному рушії невідомий, і
        // підказати його означало б навчити писати те, що публікація відхилить.
        Assert.DoesNotContain(result.Functions, f => f.Name == "ROUND");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Розширення_позначене_ярусом_і_в_Legacy_не_пропонується()
    {
        // ⛔ `Legacy` існує, щоб відтворити числа чинного рушія. Вираз, якого
        // той обчислити не міг, за визначенням нічого не відтворює, і
        // публікація його відхилить (`ECR-CALC-0433`). Тому редактор такої
        // функції в `Legacy`-версії не пропонує ВЗАГАЛІ: показати її означало б
        // запросити написати те, що не збережеться.
        Version(NumericMode.Legacy);

        var legacy = await Handler()
            .HandleAsync(ExpressionDialect.Methodology, null, 7, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(22, legacy.Functions.Count);
        Assert.All(legacy.Functions, f => Assert.Equal("Core", f.Tier));
        Assert.DoesNotContain(legacy.Functions, f => f.Name == "CONVERT");
        Assert.DoesNotContain(legacy.Functions, f => f.Name == "Ln");

        // А в `Strict` вони законні — і приходять із позначкою ярусу, щоб
        // редактор міг сказати, що це поза набором чинної системи.
        Version(NumericMode.Strict);

        var strict = await Handler()
            .HandleAsync(ExpressionDialect.Methodology, null, 7, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(26, strict.Functions.Count);
        Assert.Equal("Extension", Assert.Single(strict.Functions, f => f.Name == "Ln").Tier);
        Assert.Equal("Core", Assert.Single(strict.Functions, f => f.Name == "Pow").Tier);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Агрегат_оголошений_таким_що_приймає_діапазон()
    {
        // ⚠ Це не декоративна деталь: `SUM([7001001:7001005])` — головна форма
        // виклику агрегата в шаблоні, і підказка, яка про діапазон не знає,
        // навчала б передавати комірки по одній.
        var result = await Handler()
            .HandleAsync(ExpressionDialect.Template, null, null, CancellationToken.None)
            .ConfigureAwait(true);

        var sum = Assert.Single(result.Functions, f => f.Name == "SUM");

        Assert.True(sum.AcceptsRange);
        Assert.Null(sum.MaxArgs);
        Assert.Equal("Number", sum.ResultType);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Шапка_це_лише_ділові_ключі_і_поля_області()
    {
        // ⛔ Фільтр саме такий, як у `02b` §3.3 п. 4. Підказати звичайну
        // колонку як `HDR.` означало б навчити писати посилання, яке не
        // резолвиться, — і дізнався б про це користувач при публікації.
        Structure();

        var result = await Handler()
            .HandleAsync(ExpressionDialect.Template, templateVersionId: 1, null, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(["Train"], result.Headers.Select(h => h.Name));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Символи_методології_приходять_із_версії()
    {
        _methodologies.GetSymbolsAsync(7, Arg.Any<CancellationToken>()).Returns(
            new MethodologySymbols(
                Constants: [new MethodologySymbol("EF_CO2", null, "Fuel")],
                Formulas: [new MethodologySymbol("BaseEmission", null, null)],
                Arguments: [new MethodologySymbol("FuelConsumption", null, "Decimal")]));

        var result = await Handler()
            .HandleAsync(ExpressionDialect.Methodology, null, methodologyVersionId: 7, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Equal(["EF_CO2"], result.Constants.Select(c => c.Name));
        Assert.Equal(["BaseEmission"], result.Formulas.Select(f => f.Name));
        Assert.Equal(["FuelConsumption"], result.Arguments.Select(a => a.Name));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Без_контексту_символів_немає_і_це_не_помилка()
    {
        // ⚠ Редактор відкривають і без прив'язки до версії — щоб просто
        // подивитися на мову. Порожні переліки тут означають «контексту не
        // задано», і вигадувати символи з нічого означало б підказувати імена,
        // яких у цій версії немає.
        var result = await Handler()
            .HandleAsync(ExpressionDialect.Methodology, null, null, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Empty(result.Constants);
        Assert.Empty(result.Formulas);
        Assert.Empty(result.Arguments);
        Assert.Empty(result.Headers);
        Assert.NotEmpty(result.Functions);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private GetExpressionMetadataHandler Handler()
        => new(_versions, _methodologies, _catalogue, _access, _user);

    /// <summary>Версія методології 7 у заданому числовому режимі.</summary>
    private void Version(NumericMode mode)
    {
        var methodology = new Methodology(EcrCode.Create("WATER_DISCHARGE"), Text("Water"));
        var version = new MethodologyVersion(
            methodologyId: 1,
            version: "1.0.0.0",
            CalculationLevel.Configuration,
            createdByUserId: 7,
            Now);

        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, 7);
        version.SetModes(mode, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        methodology.AddVersion(version);

        _methodologies.FindByVersionAsync(7, Arg.Any<CancellationToken>()).Returns(methodology);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Версія з одним діловим ключем серед звичайних колонок.</summary>
    private void Structure()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Jan", isMonthColumn: true);
        builder.Column(table, "Train");

        typeof(ColumnDef).GetProperty(nameof(ColumnDef.IsBusinessKey))!
            .SetValue(table.Columns[1], true);

        var version = new TemplateVersion(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
        typeof(TemplateVersion).GetProperty(nameof(TemplateVersion.Id))!.SetValue(version, 1);
        version.GetType().GetField("_sheets", BindingFlags.Instance | BindingFlags.NonPublic)!
               .SetValue(version, new List<SheetDef> { sheet });

        _versions.GetAsync(1, Arg.Any<CancellationToken>()).Returns(version);
    }
}
