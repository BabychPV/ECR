using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>Перелік документів на реальному SQL Server (`Q-167`).</summary>
[Collection("SqlServer")]
public sealed class DocumentStoreTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Стан_погодження_читається_одним_запитом_на_сторінку()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc1 = await builder.BuildAsync(ct: CancellationToken.None);

        await using (var setup = builder.CreateContext())
        {
            var doc2 = new Document(
                doc1.ProjectId, "DOC2-Q167", 1, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));
            setup.Documents.Add(doc2);
            await setup.SaveChangesAsync(CancellationToken.None);

            // ⛔ U-03: аркуш у СКЛАДІ документа, а не лише рядок стану. Стан
            // аркуша читається від складу (`DocumentStore.SheetStatesQuery`) —
            // рівно як його рахують смуга й фільтр; `ApprovalState` на аркуш,
            // якого в документі немає, — не стан документа, а сміття даних.
            setup.DocumentSheets.Add(new DocumentSheet(doc1.DocumentId, doc1.SheetDefId));
            setup.DocumentSheets.Add(new DocumentSheet(doc2.Id, doc1.SheetDefId));

            setup.ApprovalStates.Add(new ApprovalState(doc1.DocumentId, doc1.SheetDefId, doc1.PeriodKey.Value));
            setup.ApprovalStates.Add(new ApprovalState(doc2.Id, doc1.SheetDefId, doc1.PeriodKey.Value));
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        // Рахуємо команди, які EF реально відправив, а не результат: N+1
        // на КОЖЕН документ сторінки видно лише по кількості запитів, не по
        // вмісту відповіді (той самий прийом, що й `CellStoreTests`).
        var executed = new List<string>();
        await using var counting = CreateCountingContext(executed);
        var store = new DocumentStore(counting);

        var page = await store.ListAsync(
            doc1.ProjectId, new PeriodKeyFilter(doc1.PeriodKey.Value), default, new CursorRequest(Limit: 50),
            visibleProjectIds: null,
            CancellationToken.None);

        Assert.Equal(2, page.Items.Count);

        // ⛔ Q-271: ключ словника — КОД аркуша (`SheetDef.Code`), а не
        // числовий `SheetDefId.ToString()`. Контракт задокументований у
        // `GetDocumentTablesHandler.DocumentTableDto.SheetCode` ("він же
        // ключ у DocumentSummary.SheetStates") і саме за кодом читає
        // фронтенд (`DocumentPage.tsx`: `sheetStates[s.code]`). Ключ за
        // числовим ідентифікатором ніколи не збігається з кодом — це і є
        // причина, чому бейдж статусу подання/затвердження в браузері не
        // оновлювався НІКОЛИ, попри справжню зміну стану на сервері.
        var numericKey = doc1.SheetDefId.ToString(CultureInfo.InvariantCulture);
        Assert.All(page.Items, d => Assert.True(d.SheetStates.ContainsKey(doc1.SheetCode)));
        Assert.All(page.Items, d => Assert.False(d.SheetStates.ContainsKey(numericKey)));

        // ⛔ Q-167: сторінка з ДВОМА документами — а запитів у базу рівно
        // П'ЯТЬ (перелік документів + стан погодження ВСІЄЇ сторінки + лічильники
        // останньої перевірки ВСІЄЇ сторінки, `BE-09` + пізні правки ВСІЄЇ
        // сторінки, `BE-09b` + застарілість результатів ВСІЄЇ сторінки, `resultsStale`),
        // а не по запиту на кожен документ окремо: число не залежить від розміру
        // сторінки. П'ятий запит - лише для сторінки З ПЕРІОДОМ (без періоду
        // застарілість не визначена, запиту немає).
        // ✎ B147: шостий — ключі «без погодження» періоду (`WorkflowNoneScope`), теж ОДИН на сторінку й теж лише з
        // періодом чи роком; число від розміру сторінки й від даних не залежить.
        Assert.Equal(6, executed.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_віддає_назви_аркушів_у_порядку_аркушів_тим_самим_числом_запитів()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc1 = await builder.BuildAsync(ct: CancellationToken.None);

        long doc2Id;
        await using (var setup = builder.CreateContext())
        {
            // ⚠ Порядок вставки НАВМИСНО не збігається з `Ordinal`: аркуш
            // «Last» (Ordinal 3) отримує менший Id, ніж «Middle» (Ordinal 2).
            // Порядок за Id чи за вставкою дав би [Sheet, Last, Middle] — тест
            // відрізняє саме порядок аркушів.
            var last = new SheetDef(
                doc1.TemplateVersionId, EcrCode.Create($"ZL{doc1.SheetDefId}"), En("Last sheet"), 3);
            setup.SheetDefs.Add(last);
            await setup.SaveChangesAsync(CancellationToken.None);

            var middle = new SheetDef(
                doc1.TemplateVersionId, EcrCode.Create($"ZM{doc1.SheetDefId}"), En("Middle sheet"), 2);
            setup.SheetDefs.Add(middle);
            await setup.SaveChangesAsync(CancellationToken.None);

            var doc2 = new Document(
                doc1.ProjectId, "DOC2-NAMES", 1, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc));
            setup.Documents.Add(doc2);
            await setup.SaveChangesAsync(CancellationToken.None);
            doc2Id = doc2.Id;

            foreach (var documentId in new[] { doc1.DocumentId, doc2.Id })
            {
                setup.DocumentSheets.Add(new DocumentSheet(documentId, last.Id));
                setup.DocumentSheets.Add(new DocumentSheet(documentId, doc1.SheetDefId));
                setup.DocumentSheets.Add(new DocumentSheet(documentId, middle.Id));
            }

            // Стан — лише в «Middle» і не `Draft`: так видно, що стан приїхав
            // саме до СВОГО аркуша, а не до сусіда в списку.
            var submitted = new ApprovalState(doc1.DocumentId, middle.Id, doc1.PeriodKey.Value);
            submitted.Submit(1, new DateTime(2026, 1, 16, 10, 0, 0, DateTimeKind.Utc));
            setup.ApprovalStates.Add(submitted);
            await setup.SaveChangesAsync(CancellationToken.None);
        }

        var executed = new List<string>();
        await using var counting = CreateCountingContext(executed);
        var store = new DocumentStore(counting);

        var page = await store.ListAsync(
            doc1.ProjectId, new PeriodKeyFilter(doc1.PeriodKey.Value), default, new CursorRequest(Limit: 50),
            visibleProjectIds: null,
            CancellationToken.None);

        Assert.Equal(2, page.Items.Count);

        foreach (var item in page.Items)
        {
            // ⛔ Назви — у ПОРЯДКУ аркушів (`SheetDef.Ordinal`), не вставки.
            Assert.NotNull(item.Sheets);
            Assert.Equal(
                [$"Sheet {TagOf(doc1.SheetCode)}", "Middle sheet", "Last sheet"],
                item.Sheets.Select(s => s.NameL10n.Get("en")));

            // ⛔ Друге поле не має власного джерела: ті самі коди й стани, що в словнику.
            Assert.Equal(
                item.SheetStates.OrderBy(p => p.Key, StringComparer.Ordinal),
                item.Sheets.Select(s => KeyValuePair.Create(s.Code, s.State)).OrderBy(p => p.Key, StringComparer.Ordinal));
        }

        // Аркуш без рядка стану — `Draft` (U-03), так само як у словнику.
        var second = page.Items.Single(d => d.Id == doc2Id);
        Assert.All(second.Sheets!, s => Assert.Equal("Draft", s.State));

        // ⛔ Q-167: назви приїхали тим самим пакетним запитом станів — запитів
        // на сторінку П'ЯТЬ (з застарілістю результатів, `resultsStale`), як і без назв, а не +1 на
        // документ чи на сторінку. ✎ B147: шостий — ключі «без погодження» періоду, один на сторінку.
        Assert.Equal(6, executed.Count);

        // Картка документа (`GET /documents/{id}`) віддає те саме поле тим самим запитом.
        var card = await store.FindAsync(doc1.DocumentId, new PeriodKeyFilter(doc1.PeriodKey.Value), CancellationToken.None);
        var cardSheets = card?.Sheets;
        Assert.NotNull(cardSheets);
        Assert.Equal(["Middle sheet", "Last sheet"], cardSheets.Skip(1).Select(s => s.NameL10n.Get("en")));

        // Рядок стану є лише в «Middle» — і він саме його: стан не з'їхав на сусідній аркуш.
        Assert.Equal(["Draft", "Submitted", "Draft"], cardSheets.Select(s => s.State));
    }

    /// <summary>
    /// ✎ B109: фільтр <c>templateCode</c> («Відкрити HSE400» одним запитом) — за шаблоном ДОКУМЕНТА (не проєкту): у
    /// спільному проєкті A документ основного шаблону X і документ шаблону Y (шкала пари (A, Y)) розрізняються; документ без
    /// шаблону (NULL) — основний шаблон проєкту. Невидимий проєкт B не відкривається збігом коду; невідомий код — порожньо.
    /// І запитів на сторінку стільки ж, скільки без фільтра (підзапит у тому самому запиті сторінки, без N+1).
    /// Мутаційний доказ (локально): фільтр за версією ПРОЄКТУ (<c>fn_VersionOf</c> для всіх) — червоний (a2 не знаходиться).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_за_кодом_шаблону_бере_шаблон_документа_межу_видимості_і_не_додає_запитів()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(ct: CancellationToken.None);
        var b = await builder.BuildAsync(ct: CancellationToken.None);

        long a2Id;
        string codeX;
        string codeY;
        await using (var setup = builder.CreateContext())
        {
            // Документ шаблону Y у проєкті A: шкала пари (A, Y) дає версію Y за рік (B74 PT01, D-501).
            setup.ProjectTemplateVersions.Add(new ProjectTemplateVersion(a.ProjectId, b.TemplateId, 202601, b.TemplateVersionId));
            var a2 = new Document(
                a.ProjectId, "DOC-A2-B109", 1, new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc), templateId: b.TemplateId);
            setup.Documents.Add(a2);
            await setup.SaveChangesAsync(CancellationToken.None);
            a2Id = a2.Id;

            codeX = await setup.Templates.Where(t => t.Id == a.TemplateId).Select(t => t.Code).SingleAsync();
            codeY = await setup.Templates.Where(t => t.Id == b.TemplateId).Select(t => t.Code).SingleAsync();
        }

        int[] both = [a.ProjectId, b.ProjectId];

        Assert.Equal([a.DocumentId], await IdsAsync(codeX, both));
        Assert.Equal(new[] { a2Id, b.DocumentId }.Order(), await IdsAsync(codeY, both));

        // ⛔ Межа видимості діє ПЕРШОЮ: документ невидимого проєкту B не відкривається збігом коду.
        Assert.Equal([a2Id], await IdsAsync(codeY, [a.ProjectId]));

        // Невідомий код — порожній перелік, не помилка.
        Assert.Empty(await IdsAsync("NO-SUCH-TEMPLATE-B109", both));

        // ⛔ Без N+1: з фільтром запитів стільки ж, скільки без нього (фільтр — підзапит у запиті сторінки).
        var plain = await CountQueriesAsync(null, both);
        var filtered = await CountQueriesAsync(codeY, both);
        Assert.Equal(plain, filtered);

        async Task<long[]> IdsAsync(string code, int[] visible)
        {
            await using var db = builder.CreateContext();
            var page = await new DocumentStore(db).ListAsync(
                null, new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: 2026, TemplateCode: code),
                new CursorRequest(Limit: 50), visible, CancellationToken.None);
            return [.. page.Items.Select(d => d.Id).Order()];
        }

        async Task<int> CountQueriesAsync(string? code, int[] visible)
        {
            var executed = new List<string>();
            await using var counting = CreateCountingContext(executed);
            var page = await new DocumentStore(counting).ListAsync(
                null, new PeriodKeyFilter(null), new DocumentListFilter(null, null, Year: 2026, TemplateCode: code),
                new CursorRequest(Limit: 50), visible, CancellationToken.None);
            Assert.NotEmpty(page.Items);
            return executed.Count;
        }
    }

    /// <summary>Суфікс, яким будівник позначив свої коди (<c>SHEET{tag}</c> → <c>{tag}</c>).</summary>
    private static string TagOf(string sheetCode) => sheetCode["SHEET".Length..];

    private static LocalizedText En(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Контекст, який складає кожну виконану команду в список.</summary>
    private EcrDbContext CreateCountingContext(List<string> executed)
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .LogTo(executed.Add, [RelationalEventId.CommandExecuted])
            .Options);
}
