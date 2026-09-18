// tests/Ecr.Infrastructure.Tests/Persistence/DocumentDuplicateKeyRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// `DAT-09`: два одночасні створення документа в одному проєкті отримують той
/// самий <c>BusinessKey</c>, і переможений <c>UQ_Document</c> мусить бачити
/// чистий <c>409 ECR-DOC-0409</c>, а не голий <c>500</c>.
/// </summary>
/// <remarks>
/// ⛔ Предмет. <c>DocumentStore.NextBusinessKeyAsync</c> підбирає номер
/// запитом <c>COUNT</c> плюс перевіркою «чи вільний» — знімком, який два
/// одночасні <c>POST /documents</c> бачать ОДНАКОВИМ (TOCTOU, той самий клас,
/// що Q-241/Q-245). Унікальність тримає індекс, тож другий падав на
/// <c>UQ_Document</c>, а <c>UnitOfWork.TryMapDuplicateKey</c> знав лише
/// <c>Project</c> і <c>RegistryEntry</c> — виняток ішов НЕОБРОБЛЕНИМ до
/// <c>ExceptionHandlingMiddleware</c> голим <c>500 ECR-SYS-0500</c>.
///
/// ⚠ Гонитва НЕ відтворюється реальним паралелізмом (той самий підхід, що
/// <c>RegistryEntryDuplicateRaceTests</c>): досить детерміновано відтворити
/// сам конфлікт — другий <c>INSERT</c> тим самим
/// (<c>ProjectId</c>, <c>BusinessKey</c>), тобто рівно те, з чим повернувся б
/// переможений гонитви, який уже підібрав собі ключ.
///
/// ⛔ Мутація, на якій тест зобов'язаний упасти: прибрати арм
/// <c>case Document</c> із <c>UnitOfWork.TryMapDuplicateKey</c>. Замість
/// <c>ConcurrencyConflictException</c> полетить сирий <c>DbUpdateException</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentDuplicateKeyRaceTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "DAT-09")]
    public async Task Дублікат_ключа_документа_дає_ECR_DOC_0409_а_не_сирий_виняток_бази()
    {
        var projectId = await ArrangeProjectAsync();
        var businessKey = $"DUP-{_tag}-0001";

        await using var db = Context();
        db.Documents.Add(new Document(projectId, businessKey, 9, DateTime.UtcNow));
        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);

        // Другий запис — ОКРЕМИЙ DbContext (як окремий HTTP-запит, що вже
        // підібрав собі ключ за знімком без переможця).
        await using var db2 = Context();
        db2.Documents.Add(new Document(projectId, businessKey, 9, DateTime.UtcNow));

        var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new UnitOfWork(db2).SaveChangesAsync(CancellationToken.None));

        // ⚠ Саме `ConcurrencyConflictException`, і це не педантизм про типи:
        // статус відповіді задає ТИП винятку
        // (`ExceptionHandlingMiddleware.Map`). `BusinessRuleException` доїхав
        // би клієнтові як `422` («дані невірні») — хоча дані правильні, просто
        // хтось випередив.
        Assert.Equal("ECR-DOC-0409", thrown.ErrorCode);
        Assert.Contains(businessKey, thrown.Message, StringComparison.Ordinal);
        Assert.NotNull(thrown.Details);
        Assert.Equal(businessKey, thrown.Details!["businessKey"]);
        Assert.Equal(projectId, thrown.Details["projectId"]);
    }

    /// <summary>
    /// Повторна спроба після програшу проходить: програшна сутність не лишається
    /// в трекері й не повторює той самий <c>INSERT</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього повтор у <c>CreateDocumentHandler</c> був би декорацією:
    /// невдалий <c>SaveChanges</c> НЕ прибирає сутність із трекера, тож
    /// наступний <c>SaveChanges</c> у тому самому запиті повторив би той самий
    /// конфліктний рядок — скільки б нових ключів обробник не підбирав. Це
    /// перевіряє <c>DocumentStore.AddAsync</c>, а не абстрактне знання про EF.
    ///
    /// ⛔ Мутація: прибрати відчеплення з <c>DocumentStore.AddAsync</c> —
    /// другий <c>SaveChangesAsync</c> тут упаде тим самим `ECR-DOC-0409`, хоча
    /// ключ уже інший.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "DAT-09")]
    public async Task Після_програшу_той_самий_запит_зберігає_документ_з_новим_ключем()
    {
        var projectId = await ArrangeProjectAsync();
        var sheetDefId = await ArrangeSheetAsync(projectId);
        var taken = $"RETRY-{_tag}-0001";

        await using var winner = Context();
        winner.Documents.Add(new Document(projectId, taken, 9, DateTime.UtcNow));
        await new UnitOfWork(winner).SaveChangesAsync(CancellationToken.None);

        // Один «запит» — один DbContext, одне сховище, як у DI-скоупі.
        await using var db = Context();
        var store = new DocumentStore(db);
        var uow = new UnitOfWork(db);

        // ⚠ Зі складом аркушів: саме вони й роблять відчеплення нетривіальним —
        // EF не відчіплює залежних разом із принципалом, і аркуш, що лишився
        // `Added`, дав би помилку зовнішнього ключа замість вставки.
        var loser = new Document(projectId, taken, 9, DateTime.UtcNow);
        loser.IncludeSheet(sheetDefId);
        await store.AddAsync(loser, CancellationToken.None);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => uow.SaveChangesAsync(CancellationToken.None));

        var free = $"RETRY-{_tag}-0002";
        var second = new Document(projectId, free, 9, DateTime.UtcNow);
        second.IncludeSheet(sheetDefId);
        await store.AddAsync(second, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);

        await using var check = Context();
        var keys = await check.Documents.AsNoTracking()
            .Where(d => d.ProjectId == projectId)
            .Select(d => d.BusinessKey)
            .OrderBy(k => k)
            .ToListAsync();

        Assert.Equal(new[] { taken, free }, keys);

        // Аркуш склали рівно один раз — програшний не «доїхав» другим рядком.
        var sheets = await check.DocumentSheets.AsNoTracking()
            .CountAsync(s => s.DocumentId == second.Id);
        Assert.Equal(1, sheets);
    }

    /// <summary>
    /// Другий виклик підбору ключа в межах ОДНОГО запиту не повертає той самий
    /// номер, що й перший.
    /// </summary>
    /// <remarks>
    /// ⛔ Це і є те, що робить повтор дієвим під навантаженням. Усі програвші
    /// гонитви читають той самий (уже новий) <c>COUNT</c>, і без розкиду
    /// зійшлися б на тому самому номері ще раз — і так щоразу, доки не
    /// скінчаться спроби: десять одночасних створень вишикувалися б у чергу
    /// довжиною в десять, маючи по три спроби кожне.
    ///
    /// ⛔ Мутація: прибрати розкид (<c>spread</c>) із
    /// <c>DocumentStore.NextBusinessKeyAsync</c> — обидва виклики повернуть
    /// той самий рядок, і тест почервоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "DAT-09")]
    public async Task Повторний_підбір_ключа_в_одному_запиті_не_дає_того_самого_номера()
    {
        var projectId = await ArrangeProjectAsync();

        await using var db = Context();
        var store = new DocumentStore(db);

        var first = await store.NextBusinessKeyAsync(projectId, 1, CancellationToken.None);
        var second = await store.NextBusinessKeyAsync(projectId, 1, CancellationToken.None);

        // Перший — строго послідовний: звичайне створення документа отримує
        // той самий впізнаваний номер, що й до `DAT-09`.
        Assert.Equal($"P{projectId}-V1-0001", first);
        Assert.NotEqual(first, second);
    }

    /// <summary>Аркуш у версії шаблону проєкту — склад документа посилається на нього (<c>FK_DocSheet_Sheet</c>).</summary>
    private async Task<int> ArrangeSheetAsync(int projectId)
    {
        await using var db = Context();

        var versionId = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.TemplateVersionId)
            .SingleAsync();

        var sheet = new SheetDef(versionId, EcrCode.Create($"SH_{_tag}"), Text("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        return sheet.Id;
    }

    /// <summary>Шаблон → версія → проєкт; повертає ідентифікатор проєкту.</summary>
    private async Task<int> ArrangeProjectAsync()
    {
        await using var db = Context();

        var template = new Template(EcrCode.Create($"DUP_{_tag}"), Text("Template"), 1, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        // periodPolicyId 1 — сіяна політика "ECR-Standard" (той самий факт, на
        // який спираються ProjectDuplicateCodeTests і TestDocumentBuilder).
        var project = new Project(
            EcrCode.Create($"DUPPRJ_{_tag}"), Text("Project"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        return project.Id;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
