using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Презентаційна правка «на льоту» — те, заради чого існує
/// <c>PresentationRevision</c> (ФВ-7.2). На <b>реальному</b> SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Переведено з моків на живу базу (директива №09 §8.2). Раніше
/// <c>ITemplateVersionStore</c> стояв підробкою, і головне твердження файла —
/// «патч справді міняє поле, а не лише піднімає ревізію» — доводило рівно те,
/// що обробник ВИКЛИКАВ <c>ApplyPresentationAsync</c> з правильними
/// аргументами. Що робить сам метод, не перевіряло ніщо.
///
/// ⚠ Це та сама помилка, від якої файл і з'явився, на рівень нижче: обробник
/// колись піднімав ревізію й писав аудит, не змінюючи жодного поля. Мок
/// сховища відтворював саме цю сліпу зону — реалізація могла не робити нічого,
/// і всі вісім перевірок лишалися зеленими.
///
/// ⛔ <c>ApplyPresentationAsync</c> — це сирий <c>UPDATE</c> з білим списком
/// полів і обмеженням по ВЕРСІЇ (`cfg.ColumnDef` ↔ `cfg.TableDef` ↔
/// `cfg.SheetDef`). Ні білого списку, ні ланцюга належності жоден мок не
/// відтворює: обидва — властивість SQL, а не сигнатури.
///
/// ⚠ <c>IAccessDecisionService</c> лишається підробкою: переписування 34 таких
/// місць директива §8.2 виносить за межі цього проходу. Прав тут і не
/// перевіряють — жодне твердження файла не про доступ.
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchPresentationTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Патч підпису колонки — єдина презентаційна зміна.</summary>
    private static string HeaderPatch(int columnDefId)
        => $$"""[{"entityType":"ColumnDef","entityId":{{columnDefId}},"field":"HeaderL10n","value":"{\"en\":\"Volume, m3\"}"}]""";

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.7")]
    public async Task Зміна_підпису_справді_міняє_поле_а_не_лише_піднімає_ревізію()
    {
        // ⛔ Найдорожчий різновид зеленого тесту — той, що перевіряє все
        // навколо дії, крім самої дії. Тому перевірка тут ЧИТАЄ КОЛОНКУ З
        // БАЗИ. Попередник дивився на аргументи виклику мока і був би зелений
        // навіть тоді, коли `UPDATE` не зачіпає жодного рядка.
        var version = await BareVersionAsync();

        await using var db = Context();
        var revision = await Handler(db).PatchAsync(
            version.VersionId, HeaderPatch(version.ColumnDefId), userId: 9, CancellationToken.None);

        Assert.Equal(1, revision);

        // ⛔ ОКРЕМИЙ контекст: той, у якому працював обробник, тримає версію в
        // карті ідентичності, і будь-яке читання повернуло б значення з
        // пам'яті — тобто тест зеленів би на порожньому `UPDATE`.
        await using var fresh = Context();
        var column = await fresh.ColumnDefs.AsNoTracking()
            .SingleAsync(c => c.Id == version.ColumnDefId);

        Assert.Equal("Volume, m3", column.HeaderL10n.Get("en"));

        // Ревізія теж у базі, а не лише у відповіді: саме вона є ключем кешу
        // `v{id}:r{rev}`, і різниця між ними означала б, що частина інстансів
        // віддає стару структуру.
        var stored = await fresh.TemplateVersions.AsNoTracking()
            .SingleAsync(v => v.Id == version.VersionId);

        Assert.Equal(1, stored.PresentationRevision);
        Assert.Equal($"v{version.VersionId}:r1", stored.CacheKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.2")]
    public async Task Успішний_патч_записує_структурну_зміну_в_аудит()
    {
        var version = await BareVersionAsync();

        await using var db = Context();
        await Handler(db).PatchAsync(
            version.VersionId, HeaderPatch(version.ColumnDefId), userId: 9, CancellationToken.None);

        // ⚠ Аудит читається з `aud.StructureChange`, а не з мока: журнал, у
        // якому запис лише «був викликаний», не є доказом. Таблиці немає в
        // моделі EF (створює її `11-audit-tables.sql`), тому запит сирий.
        var rows = await AuditRowsAsync(version.VersionId);

        var row = Assert.Single(rows);
        Assert.Equal("ColumnDef", row.EntityType);
        Assert.Equal(version.ColumnDefId, row.EntityId);
        Assert.Equal((byte)ChangeClass.Presentation, row.ChangeClass);
        Assert.Equal(9, row.ChangedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_типу_даних_відхиляється_з_ECR_TMPL_0409_і_нічого_не_чіпає()
    {
        var version = await BareVersionAsync();

        var patch =
            $$"""[{"entityType":"ColumnDef","entityId":{{version.ColumnDefId}},"field":"DataType","value":"String"}]""";

        await using var db = Context();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler(db).PatchAsync(version.VersionId, patch, userId: 9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);

        // Ревізія не інкрементована і тип не змінений — інакше клієнти
        // отримали б новий ключ кешу на структуру, якої не існує.
        await using var fresh = Context();
        Assert.Equal(0, (await fresh.TemplateVersions.AsNoTracking()
            .SingleAsync(v => v.Id == version.VersionId)).PresentationRevision);
        Assert.Equal(CellDataType.Decimal, (await fresh.ColumnDefs.AsNoTracking()
            .SingleAsync(c => c.Id == version.ColumnDefId)).DataType);
        Assert.Empty(await AuditRowsAsync(version.VersionId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.4")]
    public async Task Перейменування_коду_колонки_на_версії_з_документами_дає_ECR_SCHM_0409()
    {
        // ⛔ ФВ-7.4 каже дослівно: `Breaking`-зміна у версії, до якої вже
        // прив'язані документи, — це ВІДМОВА ОПЕРАЦІЇ, а не попередження. Доки
        // код `ECR-SCHM-0409` не кидав ніхто, ця зміна поверталася загальним
        // `ECR-TMPL-0409` разом із порадою «внесіть це клонуванням версії»
        // (ФВ-7.1) — і саме порада тут коштує дорого: клон із новим кодом
        // колонки НЕ рятує введені дані. Комірка посилається на код, тож після
        // переходу документів значення просто перестають знаходитися, і
        // дізнаються про це не з відмови, а з порожньої форми через місяць.
        //
        // ⚠ Документ тут СПРАВЖНІЙ: `HasDocumentsAsync` — це `JOIN` документа
        // з проєктом по `TemplateVersionId`, і мок повертав `true` замість
        // цього ланцюга. Ланцюг же і є предметом вимоги.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var patch =
            $$"""[{"entityType":"ColumnDef","entityId":{{document.ColumnDefIds[0]}},"field":"Code","value":"VOLUME_M3"}]""";

        await using var db = Context();
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler(db).PatchAsync(document.TemplateVersionId, patch, userId: 9, CancellationToken.None));

        Assert.Equal("ECR-SCHM-0409", ex.ErrorCode);

        // ⚠ Тип винятку тут — частина перевірки, а не випадковість: статус
        // відповіді береться з ТИПУ, і `BusinessRuleException` дав би 422 при
        // коді `…0409`. Саме така суперечність усередині одного коду виправлена
        // в `ECR-PRD-0422` (`P-25`), і відтворювати її новим кодом не можна.
        Assert.Equal(
            ChangeClass.Breaking,
            new ChangeClassifier().Classify("ColumnDef", "Code", hasDocuments: true));

        // Порушник названий поіменно, і факт наявності документів теж:
        // «щось структурне» не дає підстав ухвалити рішення.
        Assert.Contains("ColumnDef.Code", ex.Message, StringComparison.Ordinal);
        Assert.Equal(true, ex.Details?["hasDocuments"]);

        await using var fresh = Context();
        Assert.Equal(0, (await fresh.TemplateVersions.AsNoTracking()
            .SingleAsync(v => v.Id == document.TemplateVersionId)).PresentationRevision);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.4")]
    public async Task Те_саме_перейменування_без_документів_лишається_ФВ_7_1()
    {
        // ⛔ Друга половина ФВ-7.4, без якої перша нічого не означає: та сама
        // зміна на версії БЕЗ документів не є `Breaking` — рятувати нічого, і
        // класифікатор повертає `Safe`. Відмова лишається, але це вже ФВ-7.1
        // («опублікована версія структурно незмінна»), і порада «внесіть
        // клонуванням» тут правильна.
        //
        // ⚠ Без цього тесту перший був би зеленим і від «кидати ECR-SCHM-0409
        // на будь-яку структурну зміну» — тобто від відмови, яка забороняє те,
        // що дозволено. Тепер різницю дає САМА БАЗА: жодного документа на цій
        // версії немає, і `HasDocumentsAsync` це бачить сам.
        var version = await BareVersionAsync();

        var patch =
            $$"""[{"entityType":"ColumnDef","entityId":{{version.ColumnDefId}},"field":"Code","value":"VOLUME_M3"}]""";

        await using var db = Context();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler(db).PatchAsync(version.VersionId, patch, userId: 9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Змішаний_патч_із_однією_структурною_зміною_не_застосовує_і_дозволених()
    {
        var version = await BareVersionAsync();

        var patch = $$"""
            [{"entityType":"ColumnDef","entityId":{{version.ColumnDefId}},"field":"HeaderL10n","value":"{\"en\":\"A\"}"},
             {"entityType":"ColumnDef","entityId":{{version.ColumnDefId}},"field":"Ordinal","value":"2"},
             {"entityType":"ColumnDef","entityId":{{version.ColumnDefId}},"field":"Precision","value":"18"}]
            """;

        await using var db = Context();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler(db).PatchAsync(version.VersionId, patch, userId: 9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);

        // Порушник названий поіменно — інакше користувач шукав би його наосліп.
        Assert.Contains("Precision", ex.Message, StringComparison.Ordinal);

        // ⛔ Дві легітимні зміни з трьох НЕ застосовані В БАЗІ: патч є одним
        // цілим. Часткове застосування залишило б користувача в стані, який він
        // не замовляв і не бачить. Попередник перевіряв це по невикликаному
        // моку — тобто по наміру, а не по наслідку.
        await using var fresh = Context();
        var column = await fresh.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == version.ColumnDefId);

        Assert.Equal("Jan", column.HeaderL10n.Get("en"));
        Assert.Equal(1, column.Ordinal);
        Assert.Empty(await AuditRowsAsync(version.VersionId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_Ordinal_проходить_і_не_чіпає_нічого_крім_порядку()
    {
        var version = await BareVersionAsync();

        var patch =
            $$"""[{"entityType":"ColumnDef","entityId":{{version.ColumnDefId}},"field":"Ordinal","value":"9"}]""";

        await using var db = Context();
        var revision = await Handler(db).PatchAsync(version.VersionId, patch, userId: 9, CancellationToken.None);

        // Ordinal класифікується як презентаційний саме тому, що формули на
        // нього не спираються: діапазони розкриваються в явний список RowKey
        // при Publish, і в рантаймі діапазонів не існує (B03 §4).
        Assert.Equal(
            ChangeClass.Presentation,
            new ChangeClassifier().Classify("ColumnDef", "Ordinal", hasDocuments: true));
        Assert.Equal(1, revision);

        await using var fresh = Context();
        var column = await fresh.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == version.ColumnDefId);

        Assert.Equal(9, column.Ordinal);
        Assert.Equal("Jan", column.HeaderL10n.Get("en"));
        Assert.Equal("Jan", column.Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_чужої_версії_у_патчі_відхиляється_а_не_міняється_мовчки()
    {
        // ⛔ Перевірки, якої в моковому файлі не було й не могло бути: обмеження
        // по версії живе в `WHERE` самого `UPDATE`, і підробка сховища його не
        // має. Без нього `entityId` із мережі правив би підпис колонки ЧУЖОЇ
        // версії, а відповідь виглядала б успішною.
        var mine = await BareVersionAsync();
        var alien = await BareVersionAsync();

        await using var db = Context();
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler(db).PatchAsync(
                mine.VersionId, HeaderPatch(alien.ColumnDefId), userId: 9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);

        await using var fresh = Context();
        Assert.Equal(
            "Jan",
            (await fresh.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == alien.ColumnDefId)).HeaderL10n.Get("en"));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private PatchPresentationHandler Handler(EcrDbContext db)
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        return new PatchPresentationHandler(
            new Repository<TemplateVersion, int>(db),
            new TemplateVersionStore(db),
            new ChangeClassifier(),
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now),
            _access,
            _user);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    /// <summary>
    /// Версія з аркушем, таблицею і однією колонкою — і <b>без жодного
    /// документа</b>.
    /// </summary>
    /// <remarks>
    /// ⚠ <see cref="TestDocumentBuilder"/> тут не годиться: він заводить
    /// проєкт і документ, а половина тверджень цього файла тримається саме на
    /// тому, що документів немає (ФВ-7.4).
    /// </remarks>
    private async Task<BareVersion> BareVersionAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"PP{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(table.Id, EcrCode.Create("Jan"), Name("Jan"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column);
        await db.SaveChangesAsync();

        return new BareVersion(version.Id, table.Id, column.Id);
    }

    /// <summary>Записи <c>aud.StructureChange</c> цієї версії.</summary>
    /// <remarks>
    /// Таблиці немає в моделі EF — її створює <c>11-audit-tables.sql</c>, — тож
    /// читання сире. Це не обхід сховища, а єдиний спосіб побачити журнал.
    /// </remarks>
    private async Task<List<AuditRow>> AuditRowsAsync(int templateVersionId)
    {
        var rows = new List<AuditRow>();

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EntityType, EntityId, ChangeClass, ChangedByUserId, NewJson
            FROM   aud.StructureChange
            WHERE  TemplateVersionId = @v;
            """;
        command.Parameters.AddWithValue("@v", templateVersionId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(
                reader.GetString(0), reader.GetInt32(1), reader.GetByte(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record BareVersion(int VersionId, int TableDefId, int ColumnDefId);

    private sealed record AuditRow(
        string EntityType, int EntityId, byte ChangeClass, int ChangedByUserId, string? NewJson);
}
