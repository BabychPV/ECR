using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Reporting;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Reporting;

/// <summary>
/// Будує незмінні зрізи <c>rpt.*</c> для регламентної звітності.
/// </summary>
/// <remarks>
/// Межа з SSRS проходить саме тут: звіти лишаються в SSRS (<c>D-52</c>), а ми
/// віддаємо стабільний контракт даних. <c>rpt.*</c> — зріз **без логіки**:
/// агрегації робить цей сервіс, вʼюха лише проєктує (ФВ-0.3).
/// </remarks>
public sealed class ReportSnapshotBuilder(EcrDbContext db, IClock clock) : IReportSnapshotBuilder
{
    /// <summary>Стеля рядків одного зрізу.</summary>
    /// <remarks>
    /// Річний звіт великого проєкту — десятки тисяч рядків. Межа існує не
    /// тому, що більше не буває, а тому, що без неї помилка в правилах відбору
    /// виглядала б як повільність, а не як помилка.
    /// </remarks>
    private const int MaxRows = 200_000;

    /// <inheritdoc />
    public async Task<long> BuildAsync(
        int reportVersionId,
        int projectId,
        PeriodKey? periodKey,
        string? parametersJson,
        CancellationToken ct)
    {
        var version = await db.ReportVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == reportVersionId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Версії звіту {reportVersionId} не існує.");

        // ⚠ Статус УСПАДКОВУЄТЬСЯ від даних (D-65). Окреме поле «статус звіту»
        // стало б другим джерелом істини і рано чи пізно показало б регулятору
        // Approved на чернетці.
        var status = await StatusOfDataAsync(projectId, periodKey, ct).ConfigureAwait(false);

        var snapshot = new ReportSnapshot(
            reportVersionId, projectId, periodKey?.Value, status, clock.UtcNow, builtByUserId: null);

        db.ReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Агрегації виконуються ТУТ, одним набором запитів. Вʼюха rpt.v_*
        // нічого не рахує (ФВ-0.3): індексована вʼюха з обчисленнями не
        // перебудовується інкрементно і зупиняє запис у джерело.
        var rows = await AggregateAsync(projectId, periodKey, snapshot.Id, ct).ConfigureAwait(false);

        db.ReportRows.AddRange(rows);

        snapshot.Complete(
            rows.Count,
            Hash(rows),

            // Прогін, з якого взято числа: без нього неможливо сказати, на
            // чому стоїть значення у звіті.
            await CurrentRunAsync(projectId, periodKey, ct).ConfigureAwait(false),
            parametersJson);

        await SwitchCurrentAsync(snapshot, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return snapshot.Id;
    }

    /// <inheritdoc />
    public async Task MarkSubmittedAsync(long snapshotId, int userId, CancellationToken ct)
    {
        var snapshot = await db.ReportSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Зрізу {snapshotId} не існує.");

        // ⛔ Після подання зріз ІММУТАБЕЛЬНИЙ. Повторна побудова створює НОВИЙ
        // зріз, а не переписує цей: інакше звіт, роздрукований учора, і той
        // самий звіт сьогодні дали б різні числа без жодного сліду.
        snapshot.MarkSubmitted(userId);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SnapshotStatus> RefreshStatusAsync(long snapshotId, CancellationToken ct)
    {
        var snapshot = await db.ReportSnapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Зрізу {snapshotId} не існує.");

        // Поданий зріз статусу не міняє — це відхиляє сама сутність.
        if (snapshot.Status == SnapshotStatus.Submitted)
        {
            return snapshot.Status;
        }

        var status = await StatusOfDataAsync(
            snapshot.ProjectId,
            snapshot.PeriodKey is { } key ? new PeriodKey(key) : null,
            ct).ConfigureAwait(false);

        snapshot.RefreshStatus(status);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return status;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Найновіші першими і зі стелею. Зрізів за рік накопичуються тисячі:
    /// перелік «усіх» довелося б гортати саме тоді, коли потрібен останній.
    /// </remarks>
    public async Task<IReadOnlyList<ReportSnapshotSummary>> ListAsync(
        int? projectId, int? periodKey, CancellationToken ct)
        => await db.ReportSnapshots
            .AsNoTracking()
            .Where(s => projectId == null || s.ProjectId == projectId)
            .Where(s => periodKey == null || s.PeriodKey == periodKey)
            .OrderByDescending(s => s.BuiltAt)
            .Take(MaxSnapshots)
            .Select(s => new ReportSnapshotSummary(
                s.Id,
                s.ReportVersionId,
                s.ProjectId,
                s.PeriodKey,
                s.Status.ToString(),
                s.IsCurrent,
                s.RowCount,

                // ⚠ Сума віддається рядком. Байти в JSON перетворюються на
                // base64, який неможливо звірити очима з тим, що показує
                // SSRS, — а звіряють їх саме очима.
                s.ContentHash == null ? null : Convert.ToHexString(s.ContentHash),
                s.BuiltAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Стеля переліку зрізів.</summary>
    private const int MaxSnapshots = 500;

    /// <summary>
    /// Статус, виведений зі стану аркушів проєкту.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Approved</c> лише тоді, коли затверджені **всі** аркуші. Один
    /// незатверджений аркуш робить увесь зріз чернетковим — і це правильно:
    /// звіт, у якому половина даних погоджена, а половина ні, не є погодженим
    /// звітом (D-65).
    /// </remarks>
    private async Task<SnapshotStatus> StatusOfDataAsync(
        int projectId, PeriodKey? periodKey, CancellationToken ct)
    {
        var query =
            from state in db.ApprovalStates.AsNoTracking()
            join document in db.Documents.AsNoTracking()
                on state.DocumentId equals document.Id
            where document.ProjectId == projectId
                  && (periodKey == null || state.PeriodKey == periodKey.Value.Value)
            select state.Status;

        var statuses = await query.Take(MaxRows).ToListAsync(ct).ConfigureAwait(false);

        // Аркушів немає — зріз чернетковий. «Нічого не подано» і «все
        // затверджено» не можна плутати: перше означає порожній звіт.
        if (statuses.Count == 0)
        {
            return SnapshotStatus.Draft;
        }

        if (statuses.TrueForAll(s => s == DocumentStatus.Approved))
        {
            return SnapshotStatus.Approved;
        }

        return statuses.Exists(s => s is DocumentStatus.Submitted or DocumentStatus.Approved)
               && !statuses.Exists(s => s is DocumentStatus.Draft or DocumentStatus.Rejected)
            ? SnapshotStatus.Submitted
            : SnapshotStatus.Draft;
    }

    /// <summary>Агрегує результати розрахунку в рядки зрізу.</summary>
    private async Task<List<ReportRow>> AggregateAsync(
        int projectId, PeriodKey? periodKey, long snapshotId, CancellationToken ct)
    {
        var query =
            from result in db.CalculationResults.AsNoTracking()
            join document in db.Documents.AsNoTracking()
                on result.DocumentId equals document.Id
            join run in db.CalculationRuns.AsNoTracking()
                on result.CalculationRunId equals run.Id
            where document.ProjectId == projectId
                  && (periodKey == null || result.PeriodKey == periodKey.Value.Value)

                  // ⚠ Лише АКТУАЛЬНИЙ прогін. Без цієї умови зріз склав би
                  // результати всіх прогонів разом — числа виросли б кратно
                  // кількості перерахунків і лишилися б правдоподібними.
                  && run.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus

            // ⛔ Сортування стоїть ДО проєкції, і це не косметика. Поки
            // `OrderBy` висів на вже спроєктованому `ResultRow`, EF не міг
            // перекласти запит узагалі: `ResultRow` — тип застосунку, і
            // впорядкувати за його властивістю в SQL нема як. Побудова зрізу
            // від цього не «була повільною» — вона падала
            // `InvalidOperationException` («could not be translated») на
            // КОЖНОМУ виклику, тобто не завершилася успіхом жодного разу за
            // весь час існування `rpt.*`. Не бачив цього ніхто: `ReportDef`
            // не створювало ніщо, тож до цього рядка виконання не доходило —
            // побудова відмовляла раніше, `ECR-RPT-0404` (директива №09
            // `W7`, сценарій `S-27`).
            orderby result.DocumentId, result.SourceRowKey, result.OutputCode
            select new ResultRow(
                result.DocumentId, result.SourceRowKey, result.OutputCode,
                result.Value, result.SubstanceEntryId);

        var results = await query
            .Take(MaxRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var rows = new List<ReportRow>(results.Count * ColumnsPerResult);
        var rowNo = 0;

        foreach (var result in results)
        {
            rowNo++;
            rows.Add(Cell(snapshotId, rowNo, "DocumentId", null, result.DocumentId));
            rows.Add(Cell(snapshotId, rowNo, "RowKey", result.SourceRowKey, null));
            rows.Add(Cell(snapshotId, rowNo, "OutputCode", result.OutputCode, null));
            rows.Add(Cell(snapshotId, rowNo, "Value", null, result.Value));
            rows.Add(Cell(snapshotId, rowNo, "SubstanceEntryId", null, result.SubstanceEntryId));
        }

        return rows;
    }

    /// <summary>Скільки колонок дає один результат розрахунку.</summary>
    private const int ColumnsPerResult = 5;

    /// <summary>Актуальний прогін проєкту й періоду.</summary>
    private Task<long?> CurrentRunAsync(int projectId, PeriodKey? periodKey, CancellationToken ct)
        => db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId
                        && (periodKey == null || r.PeriodKey == periodKey.Value.Value)
                        && r.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus)
            .Select(r => (long?)r.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Робить зріз поточним, знімаючи поточність із попереднього.
    /// </summary>
    /// <remarks>
    /// ⚠ Обидві половини — в одному наборі змін, який коміт застосує разом.
    /// Між ними існує стан із двома поточними зрізами, і регуляторна вʼюха в
    /// цю мить повернула б подвоєні рядки — не помилку, а просто вдвічі більше
    /// число. Фільтрований унікальний індекс не дав би це зберегти, але вже
    /// після того, як транзакція впала б посеред побудови.
    /// </remarks>
    private async Task SwitchCurrentAsync(ReportSnapshot snapshot, CancellationToken ct)
    {
        var previous = await db.ReportSnapshots
            .Where(s => s.ReportVersionId == snapshot.ReportVersionId
                        && s.ProjectId == snapshot.ProjectId
                        && s.PeriodKey == snapshot.PeriodKey
                        && s.Id != snapshot.Id
                        && s.IsCurrent)
            .Take(MaxCurrentSnapshots)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var stale in previous)
        {
            stale.Supersede();
        }

        snapshot.MakeCurrent();
    }

    /// <summary>Стеля на кількість зрізів, з яких знімається поточність.</summary>
    /// <remarks>
    /// Поточний зріз мусить бути рівно один — це тримає фільтрований
    /// унікальний індекс. Більший список означає зіпсовані дані, і межа не дає
    /// такій зіпсованості перетворитися на довгу транзакцію.
    /// </remarks>
    private const int MaxCurrentSnapshots = 100;

    /// <summary>Комірка зрізу.</summary>
    private static ReportRow Cell(
        long snapshotId, int rowNo, string columnCode, string? text, decimal? number)
    {
        var row = new ReportRow(snapshotId, rowNo, columnCode);
        row.SetValue(text, number, null);
        return row;
    }

    /// <summary>
    /// Контрольна сума вмісту зрізу.
    /// </summary>
    /// <remarks>
    /// Рахується за ДАНИМИ у стабільному порядку, а не за часом побудови: два
    /// зрізи з однаковими числами мусять мати однакову суму, інакше нею
    /// неможливо довести, що звіт не змінився.
    /// </remarks>
    private static byte[] Hash(IReadOnlyList<ReportRow> rows)
    {
        var text = string.Join(
            '\n',
            rows.Select(r => string.Create(
                CultureInfo.InvariantCulture,
                $"{r.RowNo}|{r.ColumnCode}|{r.ValueString}|{r.ValueNumeric}")));

        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Результат розрахунку для агрегації.</summary>
    /// <remarks>
    /// Названий тип, а не анонімний: анонімний розриває вираз фігурною дужкою,
    /// і архітектурне правило «<c>ToListAsync</c> без <c>Take</c>» бачить
    /// половину інструкції без межі (`D1-08`).
    /// </remarks>
    private sealed record ResultRow(
        long DocumentId, string? SourceRowKey, string OutputCode, decimal Value, long? SubstanceEntryId);
}

/// <summary>Опис колонок звіту, що зберігається у <c>ReportVersion.ColumnsJson</c>.</summary>
/// <param name="Code">Код колонки — він же ключ у рядку зрізу.</param>
/// <param name="Kind">Тип значення: <c>text</c>, <c>number</c>, <c>date</c>.</param>
public sealed record ReportColumnSpec(string Code, string Kind)
{
    /// <summary>Налаштування розбору; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Читає опис колонок із JSON версії звіту.</summary>
    /// <param name="columnsJson">Вміст <c>ColumnsJson</c>.</param>
    /// <returns>Колонки; порожній перелік, якщо опис зламаний.</returns>
    /// <remarks>
    /// Зламаний опис не валить побудову: він ловиться при публікації версії
    /// звіту, а тут відмова зупинила б нічний прогін усіх звітів через один
    /// зіпсований.
    /// </remarks>
    public static IReadOnlyList<ReportColumnSpec> Parse(string columnsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ReportColumnSpec>>(columnsJson, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
