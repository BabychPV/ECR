using Ecr.Application.Common;
using Ecr.Application.Expressions;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
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
///
/// ⛔ Переведено з моків на живу базу (директива №09 §8.2). Символи контексту —
/// <c>CST.</c>, <c>!</c>, <c>@</c>, <c>HDR.</c> — це чотири різні ЗАПИТИ, і
/// кожен із них тест раніше просто задавав сам. Найдорожче це коштувало в двох
/// місцях: <c>HDR.</c> (фільтр «діловий ключ або поле області» — властивість
/// колонок у базі) і <c>@</c> (аргументи їдуть довгим ланцюгом версія →
/// методологія → активна прив'язка → таблиця → колонки, <c>D-69</c>). Мок
/// віддавав готову відповідь замість обох ланцюгів.
///
/// ⚠ <c>IAccessDecisionService</c> лишається підробкою: переписування 34 таких
/// місць директива §8.2 виносить за межі цього проходу.
/// </remarks>
[Collection("SqlServer")]
public sealed class ExpressionMetadataTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Діалект_шаблону_дає_рівно_свої_дванадцять_функцій()
    {
        await using var db = Context();
        var result = await Handler(db)
            .HandleAsync(ExpressionDialect.Template, null, null, CancellationToken.None);

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
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Діалект_методологій_дає_виміряний_набір_а_не_вигаданий()
    {
        // ⛔ Тест переписаний за `Q-082`. Стояло «24 функції, серед них SUM» —
        // і обидва твердження були неправдою про чинну систему: замір
        // (`tests/Ecr.Legacy.Probe`) дає 22 ядра плюс 4 розширення, а `SUM` у
        // діалекті методологій немає ЗА ПОБУДОВОЮ МОВИ — там немає діапазонів,
        // операнди скалярні. Редактор пропонував агрегат, який нема до чого
        // застосувати.
        await using var db = Context();
        var result = await Handler(db)
            .HandleAsync(ExpressionDialect.Methodology, null, null, CancellationToken.None);

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
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Розширення_позначене_ярусом_і_в_Legacy_не_пропонується()
    {
        // ⛔ `Legacy` існує, щоб відтворити числа чинного рушія. Вираз, якого
        // той обчислити не міг, за визначенням нічого не відтворює, і
        // публікація його відхилить (`ECR-CALC-0433`). Тому редактор такої
        // функції в `Legacy`-версії не пропонує ВЗАГАЛІ: показати її означало б
        // запросити написати те, що не збережеться.
        //
        // ⚠ Режим читається З БАЗИ довгим шляхом (версія → методологія →
        // потрібна версія в її переліку). Мок віддавав готову методологію, тож
        // сам цей шлях не перевіряв ніхто.
        var legacyVersion = await MethodologyVersionAsync(NumericMode.Legacy);

        await using var db = Context();
        var legacy = await Handler(db)
            .HandleAsync(ExpressionDialect.Methodology, null, legacyVersion.VersionId, CancellationToken.None);

        Assert.Equal(22, legacy.Functions.Count);
        Assert.All(legacy.Functions, f => Assert.Equal("Core", f.Tier));
        Assert.DoesNotContain(legacy.Functions, f => f.Name == "CONVERT");
        Assert.DoesNotContain(legacy.Functions, f => f.Name == "Ln");

        // А в `Strict` вони законні — і приходять із позначкою ярусу, щоб
        // редактор міг сказати, що це поза набором чинної системи.
        var strictVersion = await MethodologyVersionAsync(NumericMode.Strict);

        var strict = await Handler(db)
            .HandleAsync(ExpressionDialect.Methodology, null, strictVersion.VersionId, CancellationToken.None);

        Assert.Equal(26, strict.Functions.Count);
        Assert.Equal("Extension", Assert.Single(strict.Functions, f => f.Name == "Ln").Tier);
        Assert.Equal("Core", Assert.Single(strict.Functions, f => f.Name == "Pow").Tier);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Агрегат_оголошений_таким_що_приймає_діапазон()
    {
        // ⚠ Це не декоративна деталь: `SUM([7001001:7001005])` — головна форма
        // виклику агрегата в шаблоні, і підказка, яка про діапазон не знає,
        // навчала б передавати комірки по одній.
        await using var db = Context();
        var result = await Handler(db)
            .HandleAsync(ExpressionDialect.Template, null, null, CancellationToken.None);

        var sum = Assert.Single(result.Functions, f => f.Name == "SUM");

        Assert.True(sum.AcceptsRange);
        Assert.Null(sum.MaxArgs);
        Assert.Equal("Number", sum.ResultType);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Шапка_це_лише_ділові_ключі_і_поля_області()
    {
        // ⛔ Фільтр саме такий, як у `02b` §3.3 п. 4. Підказати звичайну
        // колонку як `HDR.` означало б навчити писати посилання, яке не
        // резолвиться, — і дізнався б про це користувач при публікації.
        //
        // ⚠ Колонки тепер СПРАВЖНІ і приходять зі сховища. Це не формальність:
        // саме тут колись і був дефект — `GET /expressions/metadata` на версії
        // з трьома колонками віддавав `"headers":[]`, бо версія вантажилася без
        // навігацій. Мок такої версії не віддавав ніколи.
        var version = await TemplateVersionAsync();

        await using var db = Context();
        var result = await Handler(db)
            .HandleAsync(ExpressionDialect.Template, version.VersionId, null, CancellationToken.None);

        Assert.Equal(["Train"], result.Headers.Select(h => h.Name));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Символи_методології_приходять_із_версії()
    {
        // ⚠ Три різні джерела, три різні запити: константи й формули належать
        // ВЕРСІЇ, а аргументи — контракт між методологією і таблицею, на якій
        // її запускають (`cfg.CalculationBinding`, `D-69`). Мок віддавав усі
        // три готовим списком, тож жоден із трьох шляхів не був перевірений.
        var version = await MethodologyVersionAsync(NumericMode.Strict, withSymbols: true);

        await using var db = Context();
        var result = await Handler(db)
            .HandleAsync(ExpressionDialect.Methodology, null, version.VersionId, CancellationToken.None);

        Assert.Equal(["EF_CO2"], result.Constants.Select(c => c.Name));
        Assert.Equal(["BaseEmission"], result.Formulas.Select(f => f.Name));
        Assert.Equal(["FuelConsumption"], result.Arguments.Select(a => a.Name));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Аргументів_немає_без_АКТИВНОЇ_прив_язки()
    {
        // ⛔ Друга половина `@`, без якої перша нічого не означає: вимкнена
        // прив'язка аргументів не дає. Аргумент — це контракт із таблицею, на
        // якій методологія ЗАПУСКАЄТЬСЯ; вимкнена прив'язка не запускається, і
        // підказати її колонки означало б назвати імена, яких у рядку джерела
        // не буде.
        //
        // ⚠ Перевірити це моком неможливо за побудовою: `IsActive` живе у
        // `WHERE` запиту, а мок повертав перелік аргументів цілком.
        var version = await MethodologyVersionAsync(NumericMode.Strict, withSymbols: true, bindingActive: false);

        await using var db = Context();
        var result = await Handler(db)
            .HandleAsync(ExpressionDialect.Methodology, null, version.VersionId, CancellationToken.None);

        Assert.Empty(result.Arguments);
        Assert.NotEmpty(result.Constants);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Без_контексту_символів_немає_і_це_не_помилка()
    {
        // ⚠ Редактор відкривають і без прив'язки до версії — щоб просто
        // подивитися на мову. Порожні переліки тут означають «контексту не
        // задано», і вигадувати символи з нічого означало б підказувати імена,
        // яких у цій версії немає.
        await using var db = Context();
        var result = await Handler(db)
            .HandleAsync(ExpressionDialect.Methodology, null, null, CancellationToken.None);

        Assert.Empty(result.Constants);
        Assert.Empty(result.Formulas);
        Assert.Empty(result.Arguments);
        Assert.Empty(result.Headers);
        Assert.NotEmpty(result.Functions);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private GetExpressionMetadataHandler Handler(EcrDbContext db)
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Calculation.View").Permission("Template.View").Build());

        return new GetExpressionMetadataHandler(
            new TemplateVersionStore(db),
            new MethodologyStore(db),
            new UnitCatalog(db),
            _access,
            _user);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    /// <summary>Версія шаблону з одним діловим ключем серед звичайних колонок.</summary>
    private async Task<TemplateFixture> TemplateVersionAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"EM{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Water"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"Main{tag}"), Name("Main"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var jan = new ColumnDef(table.Id, EcrCode.Create("Jan"), Name("Jan"), 1, CellDataType.Decimal);
        var train = new ColumnDef(table.Id, EcrCode.Create("Train"), Name("Train"), 2, CellDataType.String);
        db.ColumnDefs.Add(jan);
        db.ColumnDefs.Add(train);

        // ⚠ Публічного мутатора для `IsBusinessKey` домен не дає — ознака
        // задається при заведенні колонки. Пишеться вона через EF, а не
        // рефлексією по полю: так значення проходить тим самим мапінгом, яким
        // його потім і прочитають.
        db.Entry(train).Property(nameof(ColumnDef.IsBusinessKey)).CurrentValue = true;
        await db.SaveChangesAsync();

        return new TemplateFixture(version.Id, table.Id);
    }

    /// <summary>Версія методології в заданому числовому режимі.</summary>
    /// <param name="mode">Числовий режим версії (<c>ФВ-9.9</c>).</param>
    /// <param name="withSymbols">Завести константу, формулу і прив'язку.</param>
    /// <param name="bindingActive">Чи активна прив'язка — джерело аргументів <c>@</c>.</param>
    private async Task<MethodologyFixture> MethodologyVersionAsync(
        NumericMode mode, bool withSymbols = false, bool bindingActive = true)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var methodology = new Methodology(EcrCode.Create($"WD{tag}"), Name("Water"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0.0.0", CalculationLevel.Configuration, createdByUserId: 7, Now);
        version.SetModes(mode, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        if (!withSymbols)
        {
            return new MethodologyFixture(methodology.Id, version.Id);
        }

        var unitId = await db.Units.AsNoTracking().Select(u => u.Id).FirstAsync();

        db.MethodologyConstants.Add(
            new MethodologyConstant(version.Id, EcrCode.Create("EF_CO2"), 2.68m, unitId));
        // ⚠ Формула створюється ДОМЕНОМ (`AddFormula` тримає «правити можна
        // лише чернетку»), а в контекст додається явно: колекція версії — не
        // те, звідки її читає `GetSymbolsAsync`. Той іде по `MethodologyVersionId`
        // у `calc.MethodologyFormula`, і без явного `Add` рядка там не буде.
        var formula = version.AddFormula(
            EcrCode.Create("BaseEmission"), "1", FormulaResultType.Number, unitId);

        db.MethodologyFormulas.Add(formula);
        await db.SaveChangesAsync();

        // ⚠ Таблиця прив'язки — ОКРЕМА і з єдиною колонкою: аргументами стають
        // УСІ колонки прив'язаної таблиці, тож зайва колонка тут була б зайвим
        // аргументом у підказці.
        var (tableDefId, columnDefId) = await BindingTableAsync();

        var binding = new CalculationBinding(
            tableDefId, columnDefId, methodology.Id, "OUT", """{"by":"RowKey"}""");

        if (!bindingActive)
        {
            binding.Update("""{"by":"RowKey"}""", isActive: false);
        }

        db.CalculationBindings.Add(binding);
        await db.SaveChangesAsync();

        return new MethodologyFixture(methodology.Id, version.Id);
    }

    /// <summary>Окрема таблиця з єдиною колонкою <c>FuelConsumption</c> — для прив'язки.</summary>
    private async Task<(int TableDefId, int ColumnDefId)> BindingTableAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"EB{tag}"), Name($"Bound {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"B{tag}"), Name("Bound"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"Bnd{tag}"), Name("Bound"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(
            table.Id, EcrCode.Create("FuelConsumption"), Name("Fuel"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();

        return (table.Id, column.Id);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record TemplateFixture(int VersionId, int TableDefId);

    private sealed record MethodologyFixture(int MethodologyId, int VersionId);
}
