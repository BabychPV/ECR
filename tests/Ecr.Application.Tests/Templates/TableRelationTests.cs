using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
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
/// Зв'язки між таблицями версії: читання, запис і видалення (<c>ФВ-2.12</c>).
/// На <b>реальному</b> SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Головне правило тут не про зв'язки, а про версію: зв'язок — СТРУКТУРА,
/// бо від нього залежить, звідки в таблиці беруться числа. Тому правиться він
/// лише в чернетці (<c>ФВ-7.1</c>), а видалення у версії з документами —
/// відмова, а не попередження (<c>ФВ-7.4</c>).
///
/// ⛔ Переведено з моків на живу базу (директива №09 §8.2). Твердження файла
/// називалося «новий зв'язок ЗБЕРІГАЄТЬСЯ», а перевіряло
/// <c>_relations.Received(1).Add(...)</c> — тобто намір, а не наслідок:
/// репозиторій, який нічого не зберігає, лишав тест зеленим. Так само
/// «видалення проходить» дивилося на <c>Remove</c>, а не на зниклий рядок.
///
/// ⛔ Дві перевірки цього файла взагалі неможливі на моку, бо перевіряють
/// SQL, а не сигнатуру: належність таблиці ВЕРСІЇ (<c>ListTableCodesAsync</c> —
/// це `JOIN` через `cfg.SheetDef`; зовнішній ключ таке прийняв би, він про
/// версії не знає) і наявність документів (<c>HasDocumentsAsync</c> — `JOIN`
/// документа з проєктом). Раніше обидві відповіді просто задавав тест.
///
/// ⚠ <c>IAccessDecisionService</c> лишається підробкою: переписування 34 таких
/// місць директива §8.2 виносить за межі цього проходу.
/// </remarks>
[Collection("SqlServer")]
public sealed class TableRelationTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private const string Match = """{"by":"RowKey"}""";

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Новий_зв_язок_справді_лягає_в_базу_і_в_аудит_структурних_змін()
    {
        var version = await DraftAsync();

        await using (var db = Context())
        {
            var saved = await Save(db).HandleAsync(
                version.VersionId, version.RelationCode, Command(version), CancellationToken.None);

            Assert.Equal(version.RelationCode, saved.Code);
            Assert.Equal(version.SourceCode, saved.SourceTableCode);
            Assert.Equal(version.TargetCode, saved.TargetTableCode);
        }

        // ⛔ ОКРЕМИЙ контекст. Той, у якому працював обробник, тримає сутність
        // у карті ідентичності, і будь-яке читання повернуло б її з пам'яті —
        // тобто тест лишався б зеленим, якби `SaveChanges` не дійшов до бази.
        await using var fresh = Context();
        var stored = await fresh.TableRelations.AsNoTracking()
            .SingleAsync(r => r.SourceTableDefId == version.SourceTableDefId);

        Assert.Equal(version.RelationCode, stored.Code);
        Assert.Equal(version.TargetTableDefId, stored.TargetTableDefId);
        Assert.Equal(TableRelationKind.Rollup, stored.RelationKind);

        // ⚠ Аудит — не косметика: структурна зміна версії має слід, інакше
        // питання «хто прибрав цей rollup» не має відповіді. Читається він із
        // `aud.StructureChange`, а не з мока: журнал, у якому запис лише «був
        // викликаний», не є доказом.
        var audit = await AuditRowsAsync(version.VersionId);
        var row = Assert.Single(audit);

        Assert.Equal("TableRelationDef", row.EntityType);
        Assert.Equal("Create", row.Operation);
        Assert.Equal(stored.Id, row.EntityId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Правка_зв_язку_в_опублікованій_версії_відхиляється_і_нічого_не_лишає()
    {
        // ⛔ Зв'язок вирішує, звідки в таблиці числа. Змінити його в
        // опублікованій версії означало б змінити вже подані форми заднім
        // числом.
        var version = await DraftAsync(publish: true);

        await using (var db = Context())
        {
            var error = await Assert.ThrowsAsync<DomainException>(
                () => Save(db).HandleAsync(
                    version.VersionId, version.RelationCode, Command(version), CancellationToken.None));

            Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        }

        await using var fresh = Context();
        Assert.False(await fresh.TableRelations.AsNoTracking()
            .AnyAsync(r => r.SourceTableDefId == version.SourceTableDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Таблиця_ЧУЖОЇ_версії_у_зв_язку_відхиляється()
    {
        // ⛔ Тут стояв неіснуючий `99`, і це доводило менше, ніж здається:
        // такий зв'язок відхилив би й зовнішній ключ. Небезпечний випадок
        // інший — таблиця, яка ІСНУЄ, але належить іншій версії: зовнішній
        // ключ її прийме, бо про версії не знає, і зв'язок перетне межу
        // версії. Клон переніс би лише його половину.
        var mine = await DraftAsync();
        var alien = await DraftAsync();

        await using var db = Context();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save(db).HandleAsync(
                mine.VersionId,
                mine.RelationCode,
                new SaveTableRelationCommand(
                    mine.SourceTableDefId, alien.TargetTableDefId,
                    TableRelationKind.Rollup, Match, null, 0, true),
                CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.4")]
    public async Task Видалення_зв_язку_у_версії_З_ДОКУМЕНТАМИ_відхиляється_кодом_ECR_SCHM_0409()
    {
        // ⚠ Документ тут СПРАВЖНІЙ: «є документи» — це `JOIN` документа з
        // проєктом по `TemplateVersionId`, і саме цей ланцюг є предметом
        // ФВ-7.4. Мок повертав `true` замість нього.
        var version = await DraftAsync(withDocument: true);
        await SeedRelationAsync(version, version.RelationCode);

        await using var db = Context();
        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Delete(db).HandleAsync(version.VersionId, version.RelationCode, CancellationToken.None));

        Assert.Equal("ECR-SCHM-0409", error.ErrorCode);

        // Саме відмова, а не попередження: числа в документах рахувалися з
        // урахуванням зв'язку — і рядок мусить лишитися на місці.
        await using var fresh = Context();
        Assert.True(await fresh.TableRelations.AsNoTracking()
            .AnyAsync(r => r.Code == version.RelationCode && r.SourceTableDefId == version.SourceTableDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Видалення_зв_язку_з_чернетки_без_документів_справді_прибирає_рядок()
    {
        var version = await DraftAsync();
        await SeedRelationAsync(version, version.RelationCode);

        await using (var db = Context())
        {
            await Delete(db).HandleAsync(version.VersionId, version.RelationCode, CancellationToken.None);
        }

        await using var fresh = Context();
        Assert.False(await fresh.TableRelations.AsNoTracking()
            .AnyAsync(r => r.Code == version.RelationCode && r.SourceTableDefId == version.SourceTableDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.12")]
    public async Task Перелік_зв_язків_порожній_і_це_норма()
    {
        var version = await DraftAsync();

        await using var db = Context();
        var answer = await List(db).HandleAsync(version.VersionId, CancellationToken.None);

        // ⚠ Механізм опційний: шаблон без жодного зв'язку працює однаково.
        // Порожнеча тут — відповідь, а не незаповнена конфігурація.
        Assert.Empty(answer.Relations);

        // ⛔ І саме на порожньому переліку відповідь про стан версії ще
        // потрібна: інакше клієнт показав би кнопку «новий зв'язок» там, де
        // сервер відмовить.
        Assert.True(answer.IsEditable);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Перелік_каже_клієнту_чи_можна_правити_за_станом_версії()
    {
        var draftVersion = await DraftAsync();
        await SeedRelationAsync(draftVersion, draftVersion.RelationCode);

        await using (var db = Context())
        {
            var draft = await List(db).HandleAsync(draftVersion.VersionId, CancellationToken.None);
            Assert.True(draft.IsEditable);
            Assert.Single(draft.Relations);

            // ⚠ Коди таблиць підставляє СЕРВЕР із бази, а не тест: у моку цей
            // словник задавався руками, тож підпис зв'язку в переліку не був
            // пов'язаний із реальною структурою жодним чином.
            Assert.Equal(draftVersion.SourceCode, draft.Relations[0].SourceTableCode);
        }

        await PublishAsync(draftVersion.VersionId);

        await using var after = Context();

        // ⛔ Рахує СЕРВЕР. Клієнт, який виводив би це сам, тримав би другу
        // копію правила «опублікована незмінна».
        var published = await List(after).HandleAsync(draftVersion.VersionId, CancellationToken.None);
        Assert.False(published.IsEditable);
        Assert.Single(published.Relations);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Template_Edit_зв_язок_не_записується()
    {
        var version = await DraftAsync();

        await using var db = Context();
        var handler = Save(db, "Template.View");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(version.VersionId, version.RelationCode, Command(version), CancellationToken.None));

        await using var fresh = Context();
        Assert.False(await fresh.TableRelations.AsNoTracking()
            .AnyAsync(r => r.SourceTableDefId == version.SourceTableDefId));
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static SaveTableRelationCommand Command(DraftVersion version)
        => new(version.SourceTableDefId, version.TargetTableDefId, TableRelationKind.Rollup, Match, null, 0, true);

    private SaveTableRelationHandler Save(EcrDbContext db, string permission = "Template.Edit")
    {
        Profile(permission);

        return new SaveTableRelationHandler(
            new Repository<TemplateVersion, int>(db),
            new Repository<TableRelationDef, int>(db),
            new TemplateVersionStore(db),
            new ChangeClassifier(),
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now),
            _access,
            _user);
    }

    private DeleteTableRelationHandler Delete(EcrDbContext db)
    {
        Profile("Template.Edit");

        return new DeleteTableRelationHandler(
            new Repository<TemplateVersion, int>(db),
            new Repository<TableRelationDef, int>(db),
            new TemplateVersionStore(db),
            new ChangeClassifier(),
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now),
            _access,
            _user);
    }

    private ListTableRelationsHandler List(EcrDbContext db)
    {
        Profile("Template.View");

        return new ListTableRelationsHandler(
            new Repository<TemplateVersion, int>(db), new TemplateVersionStore(db), _access, _user);
    }

    private void Profile(string permission)
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission(permission).Permission("Template.View").Build());
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    /// <summary>Версія-чернетка з ДВОМА таблицями — джерелом і приймачем зв'язку.</summary>
    /// <param name="publish">Опублікувати версію одразу після створення.</param>
    /// <param name="withDocument">Завести проєкт і документ на цій версії.</param>
    private async Task<DraftVersion> DraftAsync(bool publish = false, bool withDocument = false)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"TR{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var source = new TableDef(
            sheet.Id, EcrCode.Create($"Main{tag}"), Name("Main"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        var target = new TableDef(
            sheet.Id, EcrCode.Create($"Cons{tag}"), Name("Consolidation"), 2,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);

        db.TableDefs.Add(source);
        db.TableDefs.Add(target);
        await db.SaveChangesAsync();

        if (withDocument)
        {
            var policyId = await db.PeriodPolicies.Select(p => p.Id).FirstAsync();

            var project = new Project(
                EcrCode.Create($"P{tag}"), Name("Project"),
                new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
                version.Id, PeriodKind.Monthly, policyId, "Asia/Almaty");
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            db.Documents.Add(new Document(project.Id, $"D{tag}", 1, Now));
            await db.SaveChangesAsync();
        }

        if (publish)
        {
            version.Publish(publishedByUserId: 8, utcNow: Now);
            await db.SaveChangesAsync();
        }

        return new DraftVersion(
            version.Id, source.Id, target.Id, source.Code, target.Code, $"RL{tag}");
    }

    /// <summary>Кладе готовий зв'язок у базу — підготовка до видалення й переліку.</summary>
    private async Task SeedRelationAsync(DraftVersion version, string code)
    {
        await using var db = Context();

        db.TableRelations.Add(new TableRelationDef(
            EcrCode.Create(code), version.SourceTableDefId, version.TargetTableDefId,
            TableRelationKind.Rollup, Match));

        await db.SaveChangesAsync();
    }

    private async Task PublishAsync(int templateVersionId)
    {
        await using var db = Context();
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == templateVersionId);
        version.Publish(publishedByUserId: 8, utcNow: Now);
        await db.SaveChangesAsync();
    }

    /// <summary>Записи <c>aud.StructureChange</c> цієї версії; таблиці немає в моделі EF.</summary>
    private async Task<List<AuditRow>> AuditRowsAsync(int templateVersionId)
    {
        var rows = new List<AuditRow>();

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EntityType, EntityId, Operation
            FROM   aud.StructureChange
            WHERE  TemplateVersionId = @v;
            """;
        command.Parameters.AddWithValue("@v", templateVersionId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditRow(reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
        }

        return rows;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <remarks>
    /// ⚠ Код зв'язку УНІКАЛЬНИЙ на всю базу (<c>UQ_TableRelationDef</c>), а не
    /// в межах версії. Спільний літерал у кількох тестах давав би падіння на
    /// вставці — і не там, де перевірка.
    /// </remarks>
    private sealed record DraftVersion(
        int VersionId, int SourceTableDefId, int TargetTableDefId,
        string SourceCode, string TargetCode, string RelationCode);

    private sealed record AuditRow(string EntityType, int EntityId, string Operation);
}
