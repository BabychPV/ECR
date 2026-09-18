// tests/Ecr.Infrastructure.Tests/Persistence/DocumentTouchConcurrencyTests.cs
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
/// `DAT-01`: «дотик» документа не робить рядок <c>doc.Document</c> точкою
/// серіалізації всіх операторів документа.
/// </summary>
/// <remarks>
/// ⛔ Предмет. <c>RowVersion</c> документа оголошено <c>IsRowVersion()</c>
/// (<c>DocumentConfiguration.cs:165</c>), а <c>DocumentStore.TouchAsync</c>
/// читав документ ВІДСТЕЖУВАНИМ — тобто «дотик» їхав у базу як
/// <c>UPDATE doc.Document … WHERE RowVersion = @rv</c>. Два оператори, що
/// пишуть у РІЗНІ таблиці одного документа, ділять рівно один рядок — цей. У
/// другого <c>UPDATE</c> знаходив 0 рядків, EF кидав
/// <c>DbUpdateConcurrencyException</c>, <c>UnitOfWork</c> перетворював його на
/// <c>ECR-CELL-0409</c>, і транзакція другого відкочувалася ЦІЛКОМ — разом із
/// даними, до яких перший не мав жодного стосунку.
///
/// ⚠ Тест НЕ відтворює гонитву реальним паралелізмом (джерело флакі-тестів,
/// той самий підхід, що <c>RowStoreRaceTests</c> і
/// <c>RegistryEntryDuplicateRaceTests</c>): досить відтворити сам порядок
/// подій — обидві одиниці роботи «торкаються» документа ДО того, як перша
/// закомітилась. Це рівно той порядок, у якому два <c>PATCH</c> проходять
/// через <c>PatchCellsHandler.PersistCoreAsync</c>.
///
/// ⛔ Мутація, на якій тест зобов'язаний упасти: повернути в
/// <c>DocumentStore.TouchAsync</c> читання відстежуваної сутності
/// (<c>FirstOrDefaultAsync</c> + <c>document.Touch(...)</c>). Другий
/// <c>SaveChangesAsync</c> кине <c>ConcurrencyConflictException</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentTouchConcurrencyTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "DAT-01")]
    public async Task Два_одночасні_дотики_одного_документа_не_дають_хибного_409()
    {
        var documentId = await ArrangeDocumentAsync(DateTime.UtcNow.AddMinutes(-10));

        // Дві одиниці роботи — два `DbContext`, як два HTTP-запити.
        await using var first = Context();
        await using var second = Context();

        var now = DateTime.UtcNow;

        // ⛔ ОБИДВА дотики — ДО першого коміту. Саме цей порядок і ламався:
        // другий бачив версію рядка, якої після коміту першого вже немає.
        await new DocumentStore(first).TouchAsync(documentId, 11, now, CancellationToken.None);
        await new DocumentStore(second).TouchAsync(documentId, 22, now, CancellationToken.None);

        await new UnitOfWork(first).SaveChangesAsync(CancellationToken.None);

        // ⛔ Доказ: другий не отримує 409 на порожньому місці. До фікса тут
        // летів `ConcurrencyConflictException("ECR-CELL-0409")`.
        var second409 = await Record.ExceptionAsync(
            () => new UnitOfWork(second).SaveChangesAsync(CancellationToken.None));

        Assert.True(
            second409 is null,
            $"другий оператор отримав відмову на дотику документа: {second409}");

        // І документ таки позначений зміненим — «не падає» саме тому, що
        // дотик відбувся, а не тому, що його прибрали.
        await using var check = Context();
        var stored = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
        Assert.True(
            stored.ModifiedAt >= now.AddSeconds(-1),
            $"ModifiedAt лишився {stored.ModifiedAt:O}, хоча документ торкали о {now:O}.");
        Assert.True(
            stored.ModifiedByUserId is 11 or 22,
            $"автором зміни лишився {stored.ModifiedByUserId}, а торкались 11 і 22.");
    }

    /// <summary>
    /// Вікно дотику назване прямо: щойно позначений документ удруге не
    /// позначається — і саме тому другий оператор не бере X-замок на його рядок.
    /// </summary>
    /// <remarks>
    /// ⚠ Це ЦІНА фікса, а не його побічний ефект, і тест тримає її видимою:
    /// дата зміни документа може відставати від правди щонайбільше на вікно
    /// (<c>DocumentStore.TouchWindowSeconds</c> = 5 с). Якщо хтось прибере
    /// вікно, цей тест почервоніє — і розмова про ціну відбудеться свідомо.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "DAT-01")]
    public async Task Дотик_у_межах_вікна_не_чіпає_рядок_документа_вдруге()
    {
        var documentId = await ArrangeDocumentAsync(DateTime.UtcNow.AddMinutes(-10));

        await using var db = Context();
        var store = new DocumentStore(db);

        var first = DateTime.UtcNow;
        await store.TouchAsync(documentId, 11, first, CancellationToken.None);

        // Другий дотик — у межах вікна (через секунду): рядок не оновлюється.
        await store.TouchAsync(documentId, 22, first.AddSeconds(1), CancellationToken.None);

        await using var check = Context();
        var afterWindow = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
        Assert.Equal(11, afterWindow.ModifiedByUserId);

        // А поза вікном — оновлюється, тобто дотик не «вимкнений назавжди».
        await store.TouchAsync(documentId, 22, first.AddSeconds(30), CancellationToken.None);

        await using var recheck = Context();
        var afterGap = await recheck.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
        Assert.Equal(22, afterGap.ModifiedByUserId);
    }

    /// <summary>Шаблон → версія → проєкт → документ; повертає ідентифікатор документа.</summary>
    private async Task<long> ArrangeDocumentAsync(DateTime createdAt)
    {
        await using var db = Context();

        var template = new Template(EcrCode.Create($"TCH_{_tag}"), Text("Template"), 1, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        // periodPolicyId 1 — сіяна політика "ECR-Standard" (той самий факт, на
        // який спираються ProjectDuplicateCodeTests і TestDocumentBuilder).
        var project = new Project(
            EcrCode.Create($"TCHPRJ_{_tag}"), Text("Project"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var document = new Document(project.Id, $"TCH-{_tag}-0001", 9, createdAt);
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        return document.Id;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
