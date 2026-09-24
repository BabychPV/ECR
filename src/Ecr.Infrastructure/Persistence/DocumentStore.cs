using System.Globalization;
using System.Linq.Expressions;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class DocumentStore(EcrDbContext db) : IDocumentStore
{
    /// <summary>Правило «усі аркуші групи обов'язкові».</summary>
    private const byte RequiresAll = 0;

    /// <summary>Правило «хоча б один аркуш групи».</summary>
    private const byte RequiresOne = 1;

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ `DAT-09`. Перед додаванням відчіплюється ПОПЕРЕДНІЙ документ, що
    /// лишився в трекері як <c>Added</c>. Це рівно шлях повтору після
    /// програної гонитви за <c>UQ_Document</c>: невдалий <c>SaveChanges</c>
    /// НЕ прибирає сутність із трекера, тож наступний <c>SaveChanges</c> у
    /// тому самому запиті повторив би ТОЙ САМИЙ конфліктний <c>INSERT</c> із
    /// тим самим ключем — скільки б ключів обробник не підбирав.
    ///
    /// ⚠ Аркуші складу відчіплюються ЯВНО: EF не відчіплює залежних разом із
    /// принципалом, і вони лишилися б <c>Added</c> із посиланням на сутність,
    /// якої в трекері вже немає (помилка зовнішнього ключа замість
    /// повторної вставки).
    ///
    /// ⚠ Область — один HTTP-запит (сховище <c>Scoped</c>), і в ньому
    /// створюється РІВНО один документ. Тому «попередній доданий документ»
    /// не може бути чиєюсь чужою незбереженою роботою.
    /// </remarks>
    public Task AddAsync(Document document, CancellationToken ct)
    {
        foreach (var stale in db.ChangeTracker.Entries<Document>()
                     .Where(e => e.State == EntityState.Added)
                     .ToList())
        {
            // ⚠ Знімок списку, а не сам список: відчеплення аркуша тягне
            // fixup EF, який ВИЛУЧАЄ його з навігації принципала — обхід
            // по живій колекції падає «Collection was modified».
            foreach (var sheet in stale.Entity.Sheets.ToList())
            {
                db.Entry(sheet).State = EntityState.Detached;
            }

            stale.State = EntityState.Detached;
        }

        db.Documents.Add(document);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<DocumentSummary?> FindAsync(
        long documentId, PeriodKeyFilter period, CancellationToken ct)
    {
        var document = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Id, d.ProjectId, d.BusinessKey, d.CreatedAt, d.NameL10n })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        var sheetCount = await db.DocumentSheets
            .CountAsync(s => s.DocumentId == documentId && s.IsIncluded, ct)
            .ConfigureAwait(false);

        var sheets = await StatesAsync(documentId, period, ct).ConfigureAwait(false);
        var late = await LateEditsBatchAsync([documentId], period, ct).ConfigureAwait(false);

        return new DocumentSummary(
            document.Id, document.ProjectId, document.BusinessKey, document.CreatedAt, sheetCount,
            ToStateMap(sheets), document.NameL10n, HasLateEdits: late.Contains(documentId),
            Sheets: sheets);
    }

    /// <inheritdoc />
    public async Task<PagedResult<DocumentSummary>> ListAsync(
        int? projectId,
        PeriodKeyFilter period,
        DocumentListFilter filter,
        CursorRequest page,
        IReadOnlyCollection<int>? visibleProjectIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var after = Cursor.Decode(page.Cursor);

        // Масив, а не `IReadOnlyCollection`: `Contains` над масивом EF
        // перекладає в `IN (...)`, над інтерфейсом — не гарантовано.
        var allowedProjects = visibleProjectIds?.ToArray();

        var documents = db.Documents
            .AsNoTracking()
            .Where(d => d.Id > after
                        && (projectId == null || d.ProjectId == projectId)
                        && (allowedProjects == null || allowedProjects.Contains(d.ProjectId)));

        // BE-09b: фільтри — у ЗАПИТІ, до `Take`; межа грантів вище лишається.
        if (filter.MineUserId is { } me)
        {
            documents = documents.Where(d => d.CreatedByUserId == me
                                             || db.ApprovalStates.Any(a => a.DocumentId == d.Id && a.SubmittedByUserId == me));
        }

        if (filter.State is { } state && period.Value is { } periodKey)
        {
            documents = WhereState(documents, state, periodKey);
        }

        // BE-09b: та сама умова, що дає позначку в рядку (`LateEditDocumentIds`),
        // застосована ЗАПИТОМ до стелі сторінки — не друге визначення «пізньої
        // правки». Без періоду діє за БУДЬ-ЯКИЙ, як і сама позначка.
        if (filter.HasLateEdits is { } wantLate)
        {
            var lateIds = LateEditDocumentIds(period);
            documents = wantLate
                ? documents.Where(d => lateIds.Contains(d.Id))
                : documents.Where(d => !lateIds.Contains(d.Id));
        }

        var rows = await documents
            .OrderBy(d => d.Id)
            .Take(page.Limit + 1)
            .Select(d => new DocumentRow(
                d.Id,
                d.ProjectId,
                d.BusinessKey,
                d.CreatedAt,
                db.DocumentSheets.Count(s => s.DocumentId == d.Id && s.IsIncluded),
                d.NameL10n,
                d.ModifiedAt,
                db.Users.Where(u => u.Id == d.ModifiedByUserId).Select(u => u.DisplayName).FirstOrDefault()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var page1 = rows.Take(page.Limit).ToList();

        // ⛔ Q-167 (аудит фази 2, продуктивність): ОДИН пакетний запит станів
        // на ВСЮ сторінку замість запиту на кожен документ — «перелік
        // документів» найчастіше відвідуваний екран системи, і зайвий похід
        // у базу на кожен рядок сторінки платить кожен, хто його відкриває.
        var statesByDocument = await StatesBatchAsync(
            [.. page1.Select(d => d.Id)], period, ct).ConfigureAwait(false);

        // BE-09: так само ОДИН запит на сторінку — лічильники останньої перевірки.
        var findingsByDocument = await LatestFindingsBatchAsync(
            [.. page1.Select(d => d.Id)], period, ct).ConfigureAwait(false);

        // BE-09b: і позначка пізніх правок — теж ОДИН запит на сторінку.
        var late = await LateEditsBatchAsync([.. page1.Select(d => d.Id)], period, ct).ConfigureAwait(false);

        var items = new List<DocumentSummary>(page1.Count);
        foreach (var d in page1)
        {
            IReadOnlyList<DocumentSheetState> sheets = statesByDocument.TryGetValue(d.Id, out var found)
                ? found
                : [];

            // ⛔ Немає підсумку — `null`, а не нуль: документ не перевіряли.
            var findings = findingsByDocument.GetValueOrDefault(d.Id);

            items.Add(new DocumentSummary(
                d.Id, d.ProjectId, d.BusinessKey, d.CreatedAt, d.SheetCount, ToStateMap(sheets), d.NameL10n,
                d.ModifiedAt, d.ModifiedByDisplayName, findings?.ErrorCount, findings?.WarningCount,
                late.Contains(d.Id), sheets));
        }

        return new PagedResult<DocumentSummary>(
            items, hasMore ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CompositionViolation>> ValidateCompositionAsync(
        int templateVersionId, IReadOnlyList<int> sheetDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sheetDefIds);

        var rules = await db.SheetGroupRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId)
            .OrderBy(r => r.Id)
            .Take(MaxRules)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rules.Count == 0)
        {
            return [];
        }

        var groups = await db.SheetDefs
            .AsNoTracking()
            .Where(s => s.TemplateVersionId == templateVersionId && s.SheetGroup != null)
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.SheetGroup })
            .Take(MaxSheets)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var chosen = sheetDefIds.ToHashSet();
        var violations = new List<CompositionViolation>();

        foreach (var rule in rules)
        {
            var inGroup = groups.Where(g => g.SheetGroup == rule.SheetGroup).Select(g => g.Id).ToList();
            if (inGroup.Count == 0)
            {
                continue;
            }

            var picked = inGroup.Count(chosen.Contains);

            // RequiresAll: група або входить цілком, або не входить зовсім.
            // Половина групи — це звіт, у якому частина форм просто відсутня,
            // і виявиться це вже в регуляторі.
            if (rule.RuleKind == RequiresAll && picked > 0 && picked < inGroup.Count)
            {
                violations.Add(new CompositionViolation(
                    rule.SheetGroup, rule.RuleKind,
                    $"обрано {picked} з {inGroup.Count}: група вимагає всі аркуші"));
            }

            if (rule.RuleKind == RequiresOne && picked == 0)
            {
                violations.Add(new CompositionViolation(
                    rule.SheetGroup, rule.RuleKind, "група вимагає хоча б один аркуш"));
            }
        }

        return violations;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SheetGroupRuleSummary>> GetGroupRulesAsync(
        int templateVersionId, CancellationToken ct)
    {
        var rules = await db.SheetGroupRules
            .AsNoTracking()
            .Where(r => r.TemplateVersionId == templateVersionId)
            .OrderBy(r => r.Id)
            .Take(MaxRules)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rules.Select(r => new SheetGroupRuleSummary(r.SheetGroup, r.RuleKind, r.TargetGroup))];
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ `DAT-09`. <c>COUNT</c> + перевірка «чи вільний» — це TOCTOU: два
    /// одночасні <c>POST</c> у той самий проєкт бачать той самий стан бази і
    /// повертаються з ОДНАКОВИМ ключем. Унікальність тримає індекс, тож один
    /// із них програє на <c>UQ_Document</c>; переможеному
    /// <c>CreateDocumentHandler</c> дає ще кілька спроб.
    ///
    /// ⛔ Але самого повтору мало: сховище живе один HTTP-запит, тож ДРУГИЙ
    /// виклик цього методу в межах одного запиту — це за визначенням повтор
    /// після програшу. Усі програвші читають той самий (уже новий) <c>COUNT</c>
    /// і без розкиду зійшлися б на тому самому номері ще раз — і так щоразу,
    /// доки не скінчаться спроби. Тому з другого виклику початок пошуку
    /// зсувається випадково, і розкид росте з кожною спробою: десять
    /// одночасних створень розходяться за один-два повтори, а не
    /// вишиковуються в чергу довжиною в десять.
    ///
    /// ⚠ Перший виклик лишається строго послідовним (<c>COUNT + 1</c>):
    /// звичайне, неконкурентне створення документа отримує той самий
    /// впізнаваний номер, що й до цієї правки. Дірки в нумерації з'являються
    /// лише там, де без них був би <c>500</c> або відмова.
    /// </remarks>
    public async Task<string> NextBusinessKeyAsync(
        int projectId, int templateVersionId, CancellationToken ct)
    {
        // ⚠ Ключ будується з проєкту і порядкового номера, а не з GUID:
        // BusinessKey потрапляє в аудит і в назви експортів, і людина мусить
        // упізнавати його з першого погляду.
        var used = await db.Documents
            .AsNoTracking()
            .CountAsync(d => d.ProjectId == projectId, ct)
            .ConfigureAwait(false);

        // ⚠ Розкид на повторі — від ОДИНИЦІ, не від нуля: нульовий зсув
        // повернув би рівно той номер, на якому запит щойно програв, тобто
        // повтор без зсуву. Верхня межа росте з кожним повтором, щоб
        // розійшлися й ті, хто програв двічі.
        var retry = _keyRequests++;
        var spread = retry == 0
            ? 0
            : System.Random.Shared.Next(1, Math.Min(MaxKeySpread, KeySpreadStep << (retry - 1)) + 1);

        for (var attempt = used + 1 + spread; attempt < used + spread + MaxKeyAttempts; attempt++)
        {
            var candidate = string.Create(
                CultureInfo.InvariantCulture, $"P{projectId}-V{templateVersionId}-{attempt:D4}");

            if (!await db.Documents
                    .AnyAsync(d => d.ProjectId == projectId && d.BusinessKey == candidate, ct)
                    .ConfigureAwait(false))
            {
                return candidate;
            }
        }

        // Унікальність тримає індекс; сюди можна дійти лише якщо хтось створює
        // документи швидше, ніж ми перебираємо номери.
        throw new Application.Errors.BusinessRuleException(
            "ECR-DOC-0409", "Не вдалося підібрати вільний бізнес-ключ документа.");
    }

    /// <inheritdoc />
    public async Task<bool> HasSheetAsync(long documentId, int sheetDefId, CancellationToken ct)
        => await db.DocumentSheets
            .AsNoTracking()
            .AnyAsync(s => s.DocumentId == documentId && s.SheetDefId == sheetDefId && s.IsIncluded, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int?> FindProjectIdAsync(long documentId, CancellationToken ct)
        => await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => (int?)d.ProjectId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<int> GetTemplateVersionIdAsync(long documentId, CancellationToken ct)
    {
        // Один запит через увесь ланцюг: документ → проєкт. Версія живе на
        // проєкті — той самий ланцюг, що в `RowStore.ResolveTableInstanceAsync`.
        var found = await (
            from document in db.Documents.AsNoTracking()
            where document.Id == documentId
            join project in db.Projects.AsNoTracking() on document.ProjectId equals project.Id
            select (int?)project.TemplateVersionId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return found ?? throw new Ecr.Application.Errors.NotFoundException(
            "ECR-DOC-0404",
            $"Документ {documentId} не знайдено.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-DOC-0404.document",
                ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ `DAT-01`. Раніше документ брався ВІДСТЕЖУВАНИМ, і «дотик» їхав у
    /// базу звичайним <c>SaveChanges</c> — тобто
    /// <c>UPDATE doc.Document … WHERE RowVersion = @rv</c>, бо
    /// <c>RowVersion</c> оголошено <c>IsRowVersion()</c>
    /// (<c>DocumentConfiguration.cs:165</c>). Наслідок: два оператори, що
    /// пишуть у РІЗНІ таблиці одного документа, конфліктували на порожньому
    /// місці. Другий <c>PATCH</c> чекав X-замка на рядку документа, після
    /// коміту першого отримував 0 оновлених рядків →
    /// <c>DbUpdateConcurrencyException</c> → <c>ECR-CELL-0409</c>, і його
    /// транзакція відкочувалася ЦІЛКОМ. Рядок документа був точкою
    /// серіалізації всіх операторів документа.
    ///
    /// ⛔ Тому тут <c>ExecuteUpdateAsync</c> БЕЗ читання і БЕЗ предиката
    /// версії: «дотик» — це не правка документа, за яку хтось змагається, а
    /// відмітка часу. <c>RowVersion</c> документа лишається чинним для
    /// СПРАВЖНІХ правок документа (склад аркушів, ім'я) — його не прибрано.
    ///
    /// ⚠ Вікно <see cref="TouchWindowSeconds"/>: якщо документ уже позначено
    /// щойно, оператор не бере X-замок на його рядок узагалі — 0 оновлених
    /// рядків тут НЕ помилка, а саме те, чого ми хочемо. Дата зміни при цьому
    /// не бреше більш ніж на це вікно.
    ///
    /// ⚠ Документа немає — тихо нічого (0 рядків). Зміна комірок неіснуючого
    /// документа відхиляється раніше, зовнішнім ключем; кидати ще й тут
    /// означало б повідомляти про ту саму помилку двічі й різними словами.
    ///
    /// ⚠ <c>ExecuteUpdateAsync</c> приєднується до вже відкритої
    /// ambient-транзакції того самого <c>DbContext</c> (`Q-243`), тобто на
    /// гарячому шляху (<c>PatchCellsHandler</c>) «дотик» і далі комітиться
    /// разом із даними. Поза транзакцією (<c>CreateRowHandler</c>) він
    /// автокомітний — рівно як сусідній <c>RowStore.TouchRowsAsync</c>, який
    /// у тому самому обробнику вже виконався до цього рядка.
    /// </remarks>
    public Task TouchAsync(long documentId, int userId, DateTime utcNow, CancellationToken ct)
    {
        // Поріг рахується ДО виразу: `utcNow.AddSeconds(-5)` усередині дерева
        // виразів EF довелося б перекладати в SQL, а тут це просто константа.
        var threshold = utcNow.AddSeconds(-TouchWindowSeconds);

        return db.Documents
            .Where(d => d.Id == documentId && d.ModifiedAt < threshold)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(d => d.ModifiedAt, utcNow)
                    .SetProperty(d => d.ModifiedByUserId, userId),
                ct);
    }

    /// <summary>
    /// Стан КОЖНОГО аркуша складу за період: рядок <c>wf.ApprovalState</c>, а
    /// де його немає — <c>Draft</c>. ЄДИНЕ місце, звідки стан аркуша беруть і
    /// картка документа (<see cref="StatesAsync"/>), і сторінка переліку
    /// (<see cref="StatesBatchAsync"/>).
    /// </summary>
    /// <remarks>
    /// ⛔ U-03. Перелік показував «—» на документі, який смуга над ним рахувала
    /// як «1 Draft», а фільтр <c>State = Draft</c> — повертав: три відповіді про
    /// стан одного документа на одному екрані. Причина — джерелом рядків тут був
    /// <c>wf.ApprovalState</c>, тобто аркуш БЕЗ рядка стану просто не потрапляв у
    /// словник, тоді як <see cref="WhereState"/> і
    /// <c>DocumentListSummaryStore</c> обидва рахують такий аркуш чернеткою
    /// (рядок стану з'являється лише з першим поданням, <c>S-17</c>).
    ///
    /// ⛔ Тому джерело рядків — СКЛАД документа (<c>doc.DocumentSheet</c>,
    /// <c>IsIncluded</c>), рівно як у тих двох; рядок стану лише ДОповнює його.
    /// Це не четверте правило, а те саме, записане на шляху читання: щоб два
    /// місця не розійшлися знову, обидва методи читають цей один запит.
    ///
    /// ⚠ Наслідок, який називаю прямо: рядок <c>ApprovalState</c> для аркуша
    /// ПОЗА складом (аркуш вилучили після подання) у словник більше не
    /// потрапляє. Так і має бути — і смуга, і фільтр його теж не бачать, а
    /// показувати стан аркуша, якого в документі немає, означало б четверту
    /// відповідь замість третьої.
    ///
    /// ⚠ Корельований підзапит, а не <c>LEFT JOIN</c> у LINQ: він перекладається
    /// в один <c>OUTER APPLY</c>, тобто запит лишається ОДИН на всю сторінку
    /// (<c>Q-167</c>) — жодного циклу по документах.
    ///
    /// ⚠ <c>PeriodKey</c> стоїть у предикаті партиційованої <c>wf.ApprovalState</c>
    /// (урок <c>WR-05</c>).
    ///
    /// ⚠ Назва й порядок аркуша (<c>SheetDef.NameL10n</c>, <c>Ordinal</c>) —
    /// з ТОГО Ж з'єднання з <c>cfg.SheetDef</c>, що вже дає код: перелік
    /// отримує назви без жодного додаткового запиту (<c>Q-167</c>). Сортування
    /// — у SQL, до <c>Take</c>: стеля обрізає хвіст, а не випадкові аркуші.
    /// </remarks>
    private IQueryable<SheetStateRow> SheetStatesQuery(long[] documentIds, int periodKey)
        => db.DocumentSheets
            .AsNoTracking()
            .Where(s => documentIds.Contains(s.DocumentId) && s.IsIncluded)
            .Join(
                db.SheetDefs,
                s => s.SheetDefId,
                d => d.Id,
                (s, d) => new { s.DocumentId, s.SheetDefId, d.Code, d.NameL10n, d.Ordinal })
            .OrderBy(s => s.DocumentId)
            .ThenBy(s => s.Ordinal)
            .ThenBy(s => s.Code)
            .Select(s => new SheetStateRow(
                s.DocumentId,
                s.Code,
                s.NameL10n,
                db.ApprovalStates
                    .Where(a => a.DocumentId == s.DocumentId
                                && a.SheetDefId == s.SheetDefId
                                && a.PeriodKey == periodKey)
                    .Select(a => (DocumentStatus?)a.Status)
                    .FirstOrDefault()));

    /// <summary>Аркуш складу, його назва і стан; <c>null</c> — рядка стану ще немає.</summary>
    private sealed record SheetStateRow(long DocumentId, string Code, LocalizedText NameL10n, DocumentStatus? Status);

    /// <summary>Рядок запиту → аркуш контракту; аркуш без рядка стану — <c>Draft</c> (<c>U-03</c>).</summary>
    private static DocumentSheetState ToSheet(SheetStateRow row)
        => new(row.Code, row.NameL10n, (row.Status ?? DocumentStatus.Draft).ToString());

    /// <summary>
    /// Словник «код → стан» із тих самих аркушів — щоб <c>SheetStates</c> і
    /// <c>Sheets</c> не могли розійтися: друге поле не має власного джерела.
    /// </summary>
    private static Dictionary<string, string> ToStateMap(IReadOnlyList<DocumentSheetState> sheets)
        => sheets.ToDictionary(s => s.Code, s => s.State, StringComparer.Ordinal);

    /// <summary>Стан аркушів за період; порожньо, якщо період не вказано.</summary>
    /// <remarks>
    /// ⛔ Q-271. Ключ словника — <c>SheetDef.Code</c>, а НЕ числовий
    /// <c>SheetDefId</c>: саме так задокументовано контракт у
    /// <c>GetDocumentTablesHandler.DocumentTableDto.SheetCode</c>
    /// ("він же ключ у DocumentSummary.SheetStates"), і саме за кодом аркуша
    /// читає словник фронтенд (`DocumentPage.tsx`: `sheetStates[s.code]`).
    /// Ключ за `SheetDefId.ToString()` (число-рядок) НІКОЛИ не збігається з
    /// кодом аркуша — пошук у фронтенді завжди промахувався, і бейдж
    /// статусу подання/затвердження не оновлювався НІКОЛИ, попри те що сам
    /// запит `/submit`/`/approve` спрацьовував і стан у базі мінявся.
    /// </remarks>
    private async Task<IReadOnlyList<DocumentSheetState>> StatesAsync(
        long documentId, PeriodKeyFilter period, CancellationToken ct)
    {
        if (period.Value is not { } periodKey)
        {
            return [];
        }

        var states = await SheetStatesQuery([documentId], periodKey)
            .Take(MaxSheets)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. states.Select(ToSheet)];
    }

    /// <summary>Стан аркушів кількох документів ОДНИМ запитом; порожньо, якщо період не вказано.</summary>
    /// <remarks>
    /// ⛔ Q-167 (аудит фази 2, продуктивність). Той самий стан, що й
    /// <see cref="StatesAsync"/> — буквально той самий запит
    /// (<see cref="SheetStatesQuery"/>), лише для сторінки документів разом:
    /// `WHERE DocumentId IN (...)`, згруповано на клієнті, а не запит на
    /// кожен документ сторінки.
    ///
    /// ⚠ Документ БЕЗ жодного аркуша складу у словнику відсутній — як і
    /// раніше. Рядка для нього тут узяти нізвідки: словник — «аркуш → стан»,
    /// а аркушів немає.
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, IReadOnlyList<DocumentSheetState>>> StatesBatchAsync(
        IReadOnlyList<long> documentIds, PeriodKeyFilter period, CancellationToken ct)
    {
        if (period.Value is not { } periodKey || documentIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<DocumentSheetState>>();
        }

        // Масив, а не `IReadOnlyList`: `Contains` над масивом EF перекладає в
        // `IN (...)`, над інтерфейсом — не гарантовано (та сама причина, що в
        // `ListAsync` для переліку проєктів).
        var states = await SheetStatesQuery([.. documentIds], periodKey)
            .Take(documentIds.Count * MaxSheets)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ `GroupBy` на клієнті зберігає порядок рядків усередині групи —
        // тобто порядок аркушів із SQL (`Ordinal`).
        return states
            .GroupBy(s => s.DocumentId)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<DocumentSheetState> (g) => [.. g.Select(ToSheet)]);
    }

    /// <summary>Лічильники ОСТАННЬОГО підсумку перевірки для сторінки документів — одним запитом.</summary>
    /// <remarks>
    /// ⚠ Читання збереженого підсумку, не повторний прогін (<c>BE-09</c>).
    /// Документа без підсумку у словнику НЕМАЄ — і саме це дає <c>null</c>.
    /// Два підсумки з однаковим <c>RunAt</c> — рідкість; береться пізніше записаний (більший <c>Id</c>).
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, LatestFindings>> LatestFindingsBatchAsync(
        IReadOnlyList<long> documentIds, PeriodKeyFilter period, CancellationToken ct)
    {
        if (period.Value is not { } periodKey || documentIds.Count == 0)
        {
            return new Dictionary<long, LatestFindings>();
        }

        var latest = await db.ValidationResults
            .AsNoTracking()
            .Where(v => documentIds.Contains(v.DocumentId)
                        && v.PeriodKey == periodKey
                        && v.RunAt == db.ValidationResults
                            .Where(x => x.DocumentId == v.DocumentId && x.PeriodKey == periodKey)
                            .Max(x => x.RunAt))
            // ⚠ Порядок визначає, КОТРИЙ із двох підсумків з однаковим `RunAt`
            // бере `g.First()` нижче: пізніше записаний (більший `Id`), а не
            // той, що план запиту віддав першим (EF 10102).
            .OrderBy(v => v.DocumentId)
            .ThenByDescending(v => v.Id)
            .Select(v => new LatestFindings(v.DocumentId, v.ErrorCount, v.WarningCount))
            .Take(documentIds.Count * MaxRunTies)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return latest
            .GroupBy(f => f.DocumentId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Фільтр зведеного стану документа за період (<c>BE-09b</c>).</summary>
    /// <remarks>
    /// ⛔ Правило — ТЕ САМЕ, що в смузі (<see cref="DocumentListSummaryStore"/>):
    /// аркуш складу без рядка стану — чернетка; хоч один відхилений — Rejected;
    /// Approved — усі аркуші. Інше правило дало б фільтр, що розходиться з цифрою
    /// над таблицею.
    /// </remarks>
    private IQueryable<Document> WhereState(IQueryable<Document> documents, DocumentStatus state, int periodKey)
    {
        var sheets = db.DocumentSheets.Where(s => s.IsIncluded);
        var states = db.ApprovalStates.Where(a => a.PeriodKey == periodKey);

        Expression<Func<Document, bool>> rejected = d => sheets.Any(s => s.DocumentId == d.Id
            && states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && a.Status == DocumentStatus.Rejected));
        Expression<Func<Document, bool>> draftSheet = d => sheets.Any(s => s.DocumentId == d.Id
            && !states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && a.Status != DocumentStatus.Draft));

        return state switch
        {
            DocumentStatus.Rejected => documents.Where(rejected),
            DocumentStatus.Draft => documents.Where(Not(rejected))
                .Where(d => !sheets.Any(s => s.DocumentId == d.Id)
                            || sheets.Any(s => s.DocumentId == d.Id
                                && !states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && a.Status != DocumentStatus.Draft))),
            DocumentStatus.Submitted => documents.Where(Not(rejected)).Where(Not(draftSheet))
                .Where(d => sheets.Any(s => s.DocumentId == d.Id
                    && states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && a.Status == DocumentStatus.Submitted))),
            DocumentStatus.Approved => documents
                .Where(d => sheets.Any(s => s.DocumentId == d.Id))
                .Where(d => !sheets.Any(s => s.DocumentId == d.Id
                    && !states.Any(a => a.DocumentId == d.Id && a.SheetDefId == s.SheetDefId && a.Status == DocumentStatus.Approved))),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Невідомий стан документа."),
        };
    }

    private static Expression<Func<T, bool>> Not<T>(Expression<Func<T, bool>> predicate)
        => Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    /// <summary>Документи з хоч однією пізньою правкою (<c>D-70</c>) — не обмежено сторінкою.</summary>
    /// <remarks>
    /// ⚠ Сирий SQL: <c>aud.CellChange</c> навмисно поза моделлю EF (незмінний журнал).
    /// Пошук іде індексом <c>IX_CellChange_Cell</c> (провідна колонка — <c>DocumentId</c>).
    /// Без періоду — пізня правка за будь-який період.
    ///
    /// ⛔ ЄДИНЕ місце, де живе предикат «пізня правка» (<c>BE-09b</c>): і
    /// позначка в рядку (<see cref="LateEditsBatchAsync"/>), і фільтр
    /// <c>hasLateEdits</c> у <see cref="ListAsync"/> компонують САМЕ цей
    /// запит — другого визначення немає. Результат лишається
    /// <c>IQueryable</c>, а не матеріалізується тут: виклик з фільтра
    /// компонується в один запит із <c>documents</c> (підзапит <c>IN</c>),
    /// виклик з позначки — звужується до сторінки нижче.
    /// </remarks>
    private IQueryable<long> LateEditDocumentIds(PeriodKeyFilter period)
    {
        var anyPeriod = period.Value is null ? 1 : 0;
        var periodKey = period.Value ?? 0;

        return db.Database
            .SqlQuery<long>($"""
                SELECT DISTINCT c.DocumentId AS Value
                  FROM aud.CellChange AS c
                 WHERE c.IsLateEdit = 1
                   AND ({anyPeriod} = 1 OR c.PeriodKey = {periodKey})
                """);
    }

    /// <summary>Документи сторінки з хоч однією пізньою правкою — одним запитом.</summary>
    private async Task<HashSet<long>> LateEditsBatchAsync(
        IReadOnlyList<long> documentIds, PeriodKeyFilter period, CancellationToken ct)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        // Масив, а не `IReadOnlyList`: та сама причина, що й `allowedProjects`
        // вище — `Contains` над масивом EF перекладає в параметризований `IN`.
        var ids = documentIds.ToArray();

        var late = await LateEditDocumentIds(period)
            .Where(id => ids.Contains(id))
            .OrderBy(id => id)
            .Take(documentIds.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. late];
    }

    /// <summary>Лічильники останньої перевірки одного документа.</summary>
    private sealed record LatestFindings(long DocumentId, int ErrorCount, int WarningCount);

    /// <summary>Скільки підсумків з однаковим <c>RunAt</c> допускаємо на документ.</summary>
    private const int MaxRunTies = 4;

    /// <summary>
    /// Рядок переліку документів.
    /// </summary>
    /// <remarks>
    /// Названий тип, а не анонімний, і це не стиль. Анонімний тип містить
    /// фігурну дужку, яка розриває вираз на два «речення» — і архітектурне
    /// правило «немає <c>ToListAsync</c> без <c>Take</c>» бачить другу
    /// половину без межі. Правило право: читати його код має бути так само
    /// легко, як людині.
    /// </remarks>
    private sealed record DocumentRow(
        long Id, int ProjectId, string BusinessKey, DateTime CreatedAt, int SheetCount,
        Domain.ValueObjects.LocalizedText? NameL10n, DateTime ModifiedAt, string? ModifiedByDisplayName);

    /// <summary>Стеля кількості правил складу в одній версії.</summary>
    private const int MaxRules = 500;

    /// <summary>Стеля кількості аркушів у версії; у чинному шаблоні їх 24.</summary>
    private const int MaxSheets = 500;

    /// <summary>Скільки номерів перебирати, шукаючи вільний ключ.</summary>
    private const int MaxKeyAttempts = 1000;

    /// <summary>Базовий розкид номера на першому повторі (`DAT-09`).</summary>
    private const int KeySpreadStep = 32;

    /// <summary>Стеля розкиду: далі номер уже нічого не каже людині.</summary>
    private const int MaxKeySpread = 256;

    /// <summary>
    /// Скільки разів у цьому запиті вже просили ключ (`DAT-09`).
    /// </summary>
    /// <remarks>
    /// ⚠ Поле екземпляра, а не статичне: сховище <c>Scoped</c>, тобто один
    /// екземпляр на HTTP-запит. Статичний лічильник розкидував би номери й
    /// там, де жодної гонитви немає.
    /// </remarks>
    private int _keyRequests;

    /// <summary>
    /// Вікно «дотику» документа в секундах (`DAT-01`).
    /// </summary>
    /// <remarks>
    /// ⚠ Компроміс названий прямо: дата зміни документа може відставати від
    /// правди щонайбільше на ці секунди. Платня за це — відсутність X-замка
    /// на рядку документа в переважній більшості <c>PATCH</c>, тобто
    /// відсутність точки, де всі оператори документа стають у чергу один за
    /// одним.
    /// </remarks>
    private const int TouchWindowSeconds = 5;
}
