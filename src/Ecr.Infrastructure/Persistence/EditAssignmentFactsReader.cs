// src/Ecr.Infrastructure/Persistence/EditAssignmentFactsReader.cs
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Читає передачі документів у вигляді фактів для <see cref="EditAssignmentGuard"/> (EA-B):
/// ОДНЕ місце проєкції для рішення про доступ і для повторної перевірки під блокуванням аркуша.
/// </summary>
/// <remarks>
/// ⛔ Передачі читаються лише З БАЗИ, на кожне рішення (профіль доступу кешується й не знає
/// документа). Рядок містить лише стан САМОГО користувача — стани й імена інших виконавців
/// правилу не потрібні й не виходять назовні (R9).
/// </remarks>
internal static class EditAssignmentFactsReader
{
    /// <summary>Передачі документів для користувача: документ → автор і факти.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentIds">Документи; для <paramref name="forUpdate"/> — рівно один.</param>
    /// <param name="userId">Для чийого стану виконавця будуються факти.</param>
    /// <param name="forUpdate">
    /// <c>true</c> — рядки передач і виконавця беруться під <c>REPEATABLEREAD, ROWLOCK</c> (повторна перевірка в
    /// транзакції запису: перехід <c>Fill/Validate/Return</c> чекає її завершення).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="utcNow">Поточний час для блокування делегатора; <c>null</c> - блокування не рахується (шлях без годинника).</param>
    public static async Task<IReadOnlyDictionary<long, (int OwnerUserId, IReadOnlyList<EditAssignmentFact> Facts)>>
        LoadAsync(
            EcrDbContext db, IReadOnlyCollection<long> documentIds, int userId, bool forUpdate, CancellationToken ct,
            DateTime? utcNow = null)
    {
        // W14F P3-4: делегатор діє, лише поки обліковий запис активний І не заблокований (LockedUntil - той самий критерій,
        // що й вхід, User.IsLockedOut). Годинник - clock служби доступу.
        // Без годинника (повторна перевірка під блокуванням аркуша - IEditAssignmentStore без IClock) блокування не рахується:
        // межа "кінець часу" робить умову LockedUntil <= now істинною для будь-якого значення. Перше рішення про дію
        // (з годинником служби доступу) блокування вже врахувало.
        var now = utcNow ?? DateTime.MaxValue;
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(documentIds);

        var result = new Dictionary<long, (int, IReadOnlyList<EditAssignmentFact>)>();
        if (documentIds.Count == 0)
        {
            return result;
        }

        if (forUpdate)
        {
            return await LoadLockedAsync(db, documentIds, userId, now, ct).ConfigureAwait(false);
        }

        var rows = await (
                from a in db.EditAssignments.AsNoTracking()
                where documentIds.Contains(a.DocumentId)
                join d in db.Documents.AsNoTracking() on a.DocumentId equals d.Id
                join u in db.Users.AsNoTracking() on a.DelegatedByUserId equals u.Id
                orderby a.DocumentId, a.Id
                select new FactRow(
                    a.DocumentId,
                    a.SheetCode,
                    a.TableCode,
                    a.MonthNumber,
                    a.PeriodKeyValue,
                    a.IsExclusive,
                    a.ValidationStatus,
                    a.DelegatedByUserId,
                    a.ExpiresAt,
                    u.IsActive && (u.LockedUntil == null || u.LockedUntil <= now),
                    d.CreatedByUserId,
                    db.EditAssignmentRecipients
                        .Where(r => r.AssignmentId == a.Id && r.UserId == userId)
                        .Select(r => (EditAssignmentRecipientStatus?)r.Status)
                        .FirstOrDefault(),
                    a.Level))
            // ⚠ Межа — стеля передач на документ (200) на кожен запитаний документ: легітимне не відсікається.
            .Take(EditAssignment.MaxPerDocument * documentIds.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Group(rows);
    }

    private sealed record FactRow(
        long DocumentId,
        string? SheetCode,
        string? TableCode,
        byte? MonthNumber,
        int? PeriodKeyValue,
        bool IsExclusive,
        EditAssignmentValidationStatus ValidationStatus,
        int DelegatedByUserId,
        DateTime? ExpiresAt,
        bool DelegatorActive,
        int OwnerUserId,
        EditAssignmentRecipientStatus? Mine,
        GrantLevel Level);

    /// <summary>
    /// Читання під блокуванням запису: передачі документа і рядок виконавця — кожне під
    /// <c>REPEATABLEREAD, ROWLOCK</c> (спільні блокування до коміту).
    /// </summary>
    /// <remarks>
    /// ⛔ P1-R1/R2 (аудит). Раніше `UPDLOCK` стояв лише на <c>doc.EditAssignment</c>, а стан виконавця
    /// читався підзапитом по <c>doc.EditAssignmentRecipient</c> БЕЗ підказки: під RCSI він бачив
    /// знімок на початок statement, і «Заповнено»/зняття виконавця, зафіксовані під час очікування
    /// блокування, не було видно — правка проходила. Тепер обидві таблиці читаються окремими
    /// statement-ами зі спільним блокуванням, що тримається до коміту: перехід станів (U/X на цих
    /// рядках) чекає завершення запису, а вже зафіксований перехід видно (блокуюче читання не
    /// використовує знімок).
    ///
    /// ⚠ Спільне, а не оновлювальне: два записи по одному документу не серіалізуються (U несумісний
    /// з U) — серіалізується лише перехід станів проти запису.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<long, (int OwnerUserId, IReadOnlyList<EditAssignmentFact> Facts)>>
        LoadLockedAsync(
            EcrDbContext db, IReadOnlyCollection<long> documentIds, int userId, DateTime now, CancellationToken ct)
    {
        var documentId = documentIds.Single();

        var assignments = await db.EditAssignments
            .FromSql($"SELECT * FROM doc.EditAssignment WITH (REPEATABLEREAD, ROWLOCK) WHERE DocumentId = {documentId}")
            .AsNoTracking()
            .OrderBy(a => a.Id)
            .Take(EditAssignment.MaxPerDocument)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var empty = new Dictionary<long, (int, IReadOnlyList<EditAssignmentFact>)>();
        if (assignments.Count == 0)
        {
            return empty;
        }

        // ⛔ P2-N3: ідентифікатори — з першого statement, а не підзапитом по документу: з підзапитом
        // оптимізатор міг піти через IX_EditAssignmentRecipient_User (UserId, Status) і узяти спільні
        // блокування на ВСІ рядки виконавця в ІНШИХ документах — тобто заблокувати чужі Fill. Тут —
        // пошук за первинним ключем (AssignmentId, UserId), не більше MaxPerDocument ключів.
        var idsJson = System.Text.Json.JsonSerializer.Serialize(assignments.Select(a => a.Id));

        var mine = await db.EditAssignmentRecipients
            // ⛔ P2-N3 (CI integ-1011-k): `WHERE UserId = @u AND AssignmentId IN (…)` оптимізатор виконував через
            // IX_EditAssignmentRecipient_User (UserId, Status) і брав S на рядки виконавця в ІНШИХ документах.
            // Тут — вибірка за ПАРАМИ (AssignmentId, UserId): OPENJSON з id першого statement, вкладені цикли й
            // індекс PK примусово — кожен рядок береться пошуком по первинному ключу (AssignmentId, UserId).
            .FromSql($"""
                SELECT r.*
                FROM OPENJSON({idsJson}) AS j
                INNER LOOP JOIN doc.EditAssignmentRecipient AS r
                    WITH (REPEATABLEREAD, ROWLOCK, INDEX(PK_EditAssignmentRecipient))
                    ON r.AssignmentId = CAST(j.[value] AS bigint) AND r.UserId = {userId}
                """)
            .AsNoTracking()
            .OrderBy(r => r.AssignmentId)
            .Take(EditAssignment.MaxPerDocument)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var statusById = mine.ToDictionary(r => r.AssignmentId, r => r.Status);

        var ownerUserId = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => d.CreatedByUserId)
            .SingleAsync(ct)
            .ConfigureAwait(false);

        // ⚠ Активність делегатора і автор документа читаються БЕЗ блокування: вимкнення делегатора
        // не піднімає права, а лише знімає підйом; читання — зафіксований стан на момент statement
        // (вікно мілісекунд, без ескалації) — прийнято (docs/build/02-contracts.md, розділ EA).
        var delegatorIds = assignments.Select(a => a.DelegatedByUserId).Distinct().ToList();
        var active = await db.Users.AsNoTracking()
            .Where(u => delegatorIds.Contains(u.Id))
            .Select(u => new { u.Id, Active = u.IsActive && (u.LockedUntil == null || u.LockedUntil <= now) })
            .ToDictionaryAsync(u => u.Id, u => u.Active, ct)
            .ConfigureAwait(false);

        var rows = assignments.Select(a => new FactRow(
            a.DocumentId,
            a.SheetCode,
            a.TableCode,
            a.MonthNumber,
            a.PeriodKeyValue,
            a.IsExclusive,
            a.ValidationStatus,
            a.DelegatedByUserId,
            a.ExpiresAt,
            active.GetValueOrDefault(a.DelegatedByUserId),
            ownerUserId,
            statusById.TryGetValue(a.Id, out var status) ? status : (EditAssignmentRecipientStatus?)null,
            a.Level));

        return Group(rows);
    }

    private static Dictionary<long, (int OwnerUserId, IReadOnlyList<EditAssignmentFact> Facts)> Group(
        IEnumerable<FactRow> rows)
    {
        var result = new Dictionary<long, (int, IReadOnlyList<EditAssignmentFact>)>();
        foreach (var group in rows.GroupBy(r => r.DocumentId))
        {
            var facts = group.Select(ToFact).ToList();

            result[group.Key] = (group.First().OwnerUserId, facts);
        }

        return result;
    }

    private static EditAssignmentFact ToFact(FactRow r)
        => new(
            r.SheetCode,
            r.TableCode,
            r.MonthNumber,
            r.PeriodKeyValue is { } p ? new PeriodKey(p) : null,
            r.IsExclusive,
            r.ValidationStatus == EditAssignmentValidationStatus.Validated,
            r.DelegatorActive,
            r.DelegatedByUserId,
            r.ExpiresAt,
            r.Mine is { } status ? ToProgress(status) : null,
            r.Level);

    /// <summary>Передачі ОДНОГО документа для кількох користувачів (AN-59): спільні факти й стан кожного виконавця.</summary>
    /// <param name="OwnerUserId">Автор документа.</param>
    /// <param name="Shared">Факти передач зі станом виконавця <c>null</c> (від користувача не залежать).</param>
    /// <param name="ByUser">Користувач → ті самі факти, зі станом САМЕ цього користувача (позиція в позицію з <paramref name="Shared"/>).</param>
    public sealed record ManyFacts(
        int OwnerUserId, IReadOnlyList<EditAssignmentFact> Shared,
        IReadOnlyDictionary<int, IReadOnlyList<EditAssignmentFact>> ByUser);

    /// <summary>
    /// <see cref="LoadAsync"/> (без блокування) для КІЛЬКОХ користувачів одним документом: два запити на весь виклик —
    /// передачі з автором і делегаторами, рядки виконавців усіх користувачів. Той самий склад і порядок фактів, що
    /// у поштучному читанні (тест еквівалентності — <c>CanApproveManyEquivalenceTests</c>).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="userIds">Користувачі.</param>
    /// <param name="utcNow">Поточний час для блокування делегатора.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>null</c> — передач у документі немає.</returns>
    public static async Task<ManyFacts?> LoadManyAsync(
        EcrDbContext db, long documentId, IReadOnlyCollection<int> userIds, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(userIds);

        var assignments = await (
                from a in db.EditAssignments.AsNoTracking()
                where a.DocumentId == documentId
                join d in db.Documents.AsNoTracking() on a.DocumentId equals d.Id
                join u in db.Users.AsNoTracking() on a.DelegatedByUserId equals u.Id
                orderby a.Id
                select new
                {
                    a.Id,
                    a.DocumentId,
                    a.SheetCode,
                    a.TableCode,
                    a.MonthNumber,
                    a.PeriodKeyValue,
                    a.IsExclusive,
                    a.ValidationStatus,
                    a.DelegatedByUserId,
                    a.ExpiresAt,
                    DelegatorActive = u.IsActive && (u.LockedUntil == null || u.LockedUntil <= utcNow),
                    Owner = d.CreatedByUserId,
                    a.Level,
                })
            .Take(EditAssignment.MaxPerDocument)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (assignments.Count == 0)
        {
            return null;
        }

        var ids = assignments.Select(a => a.Id).ToList();
        var users = userIds.Distinct().ToList();
        var mine = (await db.EditAssignmentRecipients.AsNoTracking()
                .Where(r => users.Contains(r.UserId) && ids.Contains(r.AssignmentId))
                .Select(r => new { r.AssignmentId, r.UserId, r.Status })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToLookup(r => r.UserId);

        var shared = assignments
            .Select(a => ToFact(new FactRow(
                a.DocumentId, a.SheetCode, a.TableCode, a.MonthNumber, a.PeriodKeyValue, a.IsExclusive,
                a.ValidationStatus, a.DelegatedByUserId, a.ExpiresAt, a.DelegatorActive, a.Owner, null, a.Level)))
            .ToList();

        var byUser = new Dictionary<int, IReadOnlyList<EditAssignmentFact>>(users.Count);
        foreach (var user in users)
        {
            var statuses = mine[user].ToDictionary(r => r.AssignmentId, r => r.Status);
            byUser[user] = assignments
                .Select(a => ToFact(new FactRow(
                    a.DocumentId, a.SheetCode, a.TableCode, a.MonthNumber, a.PeriodKeyValue, a.IsExclusive,
                    a.ValidationStatus, a.DelegatedByUserId, a.ExpiresAt, a.DelegatorActive, a.Owner,
                    statuses.TryGetValue(a.Id, out var status) ? status : (EditAssignmentRecipientStatus?)null, a.Level)))
                .ToList();
        }

        return new ManyFacts(assignments[0].Owner, shared, byUser);
    }

    /// <summary>
    /// D-443: документи, у яких користувач має ДІЮЧЕ надання учасника — виконавець передачі, строк не минув,
    /// делегатор активний (ті самі умови, що <c>DocumentParticipation.Active</c> над фактами).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="userId">Учасник.</param>
    /// <param name="utcNow">Поточний час (строк дії).</param>
    /// <returns>Незавершений запит ідентифікаторів документів — для підзапиту переліку й перевірок видимості.</returns>
    /// <remarks>
    /// ⚠ Пошук іде від <c>IX_EditAssignmentRecipient_User (UserId, Status) INCLUDE (AssignmentId)</c> до передачі за
    /// ключем: учасник має одиниці-десятки надань, тож підзапит у переліку документів не сканує передачі.
    /// </remarks>
    public static IQueryable<long> ParticipatedDocuments(EcrDbContext db, int userId, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(db);

        return from r in db.EditAssignmentRecipients.AsNoTracking()
               where r.UserId == userId
               join a in db.EditAssignments.AsNoTracking() on r.AssignmentId equals a.Id
               where (a.ExpiresAt == null || a.ExpiresAt > utcNow)
                     && db.Users.Any(u => u.Id == a.DelegatedByUserId && u.IsActive && (u.LockedUntil == null || u.LockedUntil <= utcNow))
               select a.DocumentId;
    }

    private static AssigneeProgress ToProgress(EditAssignmentRecipientStatus status) => status switch
    {
        EditAssignmentRecipientStatus.Filled => AssigneeProgress.Filled,
        EditAssignmentRecipientStatus.Returned => AssigneeProgress.Returned,
        _ => AssigneeProgress.Assigned,
    };
}
