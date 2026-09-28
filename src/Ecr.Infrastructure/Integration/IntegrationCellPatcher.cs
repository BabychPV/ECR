// src/Ecr.Infrastructure/Integration/IntegrationCellPatcher.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Security;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Integration;

/// <summary>
/// Запис комірок від інтеграції через <b>спільний</b> обробник (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ Тут немає жодного власного SQL і не має бути. Директива забороняє другий
/// шлях запису в комірки прямо, і причина названа: саме другий шлях дав
/// `A7-27` — мапа колонок будувалася там інакше, ніж на основному, і
/// адресація розійшлася. Ця реалізація лише **готує запит** і віддає його
/// тому самому <see cref="PatchCellsHandler"/>, яким пише людина.
///
/// ⚠ Комірки з правкою людини відсіюються ДО виклику, а не після: обробник не
/// знає про походження попереднього значення, і питати його про це означало б
/// навчити основний шлях правилам інтеграції.
///
/// ⛔ Наявний рядок адресується ЙОГО поточною версією, <c>null</c> — лише
/// рядок, якого ще немає (R-B2: <c>null</c> = «створити»). Доти тут стояв
/// <c>null</c> завжди, і запис у НАЯВНИЙ рядок — фіксовані рядки заводяться
/// при відкритті періоду, а будь-який другий прогін пише туди ж — падав
/// <c>ECR-ROW-0409</c> «рядки з такими ключами вже існують» на весь прогін.
///
/// ⚠ Рядок змінили між читанням версії і записом (<c>ECR-CELL-0409</c>) —
/// обмежений повтор: перечитати стан і спробувати знову, не більше
/// <see cref="MaxAttempts"/> разів. Після цього комірки повертаються в
/// <see cref="IntegrationWriteResult.WriteConflicts"/> — і задача кладе їх у
/// журнал покриття статусом <c>SkippedWriteConflict</c>, а не валить прогін
/// винятком (<c>D-118</c>: «зібрано, але не записано» — не мовчки, але й не
/// аварія). ⛔ Не в <see cref="IntegrationWriteResult.KeptManual"/>: людина
/// комірку не правила, і журнал писав би «має правку людини» неправду.
///
/// ⚠ Незмінні значення відсіюються тут, до обробника (ідемпотентність
/// повторного прогону). Обробник сам не дає рядка аудиту на незмінне
/// значення (`U-22`), але комірку однаково ПИШЕ: версія рядка, «дотик»
/// документа і задача перерахунку — на кожен прогін, у якому нічого не
/// змінилося.
/// </remarks>
public sealed class IntegrationCellPatcher(
    EcrDbContext db,
    IRowStore rowStore,
    ICellStore cellStore,
    PatchCellsHandler patch,
    IAccessDecisionService access,
    ICurrentUser currentUser) : ICellPatcher
{
    /// <summary>Скільки разів пробувати запис, якщо рядок змінили між читанням і записом.</summary>
    public const int MaxAttempts = 3;

    /// <inheritdoc />
    public async Task<IntegrationWriteResult> ApplyIntegrationAsync(
        long documentId,
        long tableInstanceId,
        PeriodKey periodKey,
        IReadOnlyList<IntegrationCellValue> cells,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cells);

        if (cells.Count == 0)
        {
            return new IntegrationWriteResult(0, []);
        }

        // ⚠ Коди колонок беруться з опису таблиці, а не з мапінгу: мапінг
        // зберігає `ColumnDefId`, а шлях запису адресує КОДОМ. Переклад робимо
        // тут і в межах ЦІЄЇ таблиці — саме звуження до таблиці й було
        // виправленням `A7-27`.
        var instance = await db.TableInstances
            .AsNoTracking()
            .Where(t => t.Id == tableInstanceId && t.PeriodKeyValue == periodKey.Value)
            .Select(t => new { t.TableDefId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Екземпляра таблиці {tableInstanceId} за період {periodKey.Value} не існує.");

        var columns = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => c.TableDefId == instance.TableDefId && !c.IsDeleted)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var columnById = columns.ToDictionary(c => c.Id);

        for (var attempt = 1; ; attempt++)
        {
            var plan = await PlanAsync(tableInstanceId, periodKey, cells, columnById, ct).ConfigureAwait(false);

            if (plan.Rows.Count == 0)
            {
                return new IntegrationWriteResult(0, plan.Kept, plan.AwaitingConfirmation);
            }

            try
            {
                await patch
                    .HandleAsync(
                        new PatchCellsRequest(tableInstanceId, periodKey.Value, "Integration", plan.Rows),
                        ct)
                    .ConfigureAwait(false);

                return new IntegrationWriteResult(plan.Applied, plan.Kept, plan.AwaitingConfirmation);
            }
            catch (EcrException ex) when (IsRowRace(ex))
            {
                // ⚠ Відкинутий батч міг лишити в трекері контексту зміни, яких
                // у базі вже немає (транзакцію запису відкочено), — наступна
                // спроба не має їх дописувати.
                db.ChangeTracker.Clear();

                if (attempt >= MaxAttempts)
                {
                    return new IntegrationWriteResult(
                        0,
                        plan.Kept,
                        plan.AwaitingConfirmation,
                        [.. plan.Rows.SelectMany(r => r.Cells.Select(c => $"{r.RowKey}:{c.ColumnCode}"))]);
                }
            }
        }
    }

    /// <summary>Те, що піде в обробник за одну спробу, і те, що лишено.</summary>
    private sealed record WritePlan(List<PatchRow> Rows, List<string> Kept, int Applied, List<string> AwaitingConfirmation);

    /// <summary>
    /// Чи це гонка за рядок, яку знімає перечитування: версію змінили
    /// (<c>ECR-CELL-0409</c>) або рядок, якого не було, щойно створили
    /// (<c>ECR-ROW-0409</c> «ключі вже існують»).
    /// </summary>
    private static bool IsRowRace(EcrException ex)
        => string.Equals(ex.ErrorCode, ErrorCodes.CellConflict, StringComparison.Ordinal)
           || (string.Equals(ex.ErrorCode, ErrorCodes.RowDuplicate, StringComparison.Ordinal)
               && ex.Details?.GetValueOrDefault("messageKey") is "err.ECR-ROW-0409.rowKeysExist");

    /// <summary>Будує батч за ПОТОЧНИМ станом таблиці.</summary>
    /// <remarks>
    /// ⛔ Порядок читань — частина правила <c>D-118</c>, а не стиль. Спершу
    /// ВЕРСІЇ рядків, потім правки людини, потім значення. Людина, що встигла
    /// до читання версій, видна в журналі; людина, що встигла після, змінила
    /// версію рядка, і обробник відхилить батч (<c>ECR-CELL-0409</c>) — а
    /// повтор уже побачить її правку. Навпаки (журнал, потім версії) лишало б
    /// вікно, у якому правку людини мовчки затирає інтеграція.
    /// </remarks>
    private async Task<WritePlan> PlanAsync(
        long tableInstanceId,
        PeriodKey periodKey,
        IReadOnlyList<IntegrationCellValue> cells,
        Dictionary<int, ColumnDef> columnById,
        CancellationToken ct)
    {
        var existing = (await rowStore.GetRowsAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false))
            .ToDictionary(r => r.RowKey, StringComparer.Ordinal);

        // ⛔ Комірки, що їх правила людина, не чіпаємо (`D-118`). Ознака —
        // походження останньої зміни в журналі комірок: `UserEdit` означає
        // свідоме рішення, і інтеграція не має права його стерти.
        var manual = await ManualCellsAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);

        var candidates = new List<(IntegrationCellValue Cell, ColumnDef Column)>();
        var kept = new List<string>();

        foreach (var cell in cells)
        {
            if (!columnById.TryGetValue(cell.ColumnDefId, out var column))
            {
                // Колонки немає серед живих колонок цієї таблиці. Мапінги
                // чужих таблиць сюди вже не доходять (задача звужує їх до
                // таблиці екземпляра, суміжне D16-03), тож лишається
                // видалена колонка — помилка конфігурації, і мовчати про
                // неї не можна, але й падати посеред перенесення теж.
                kept.Add($"{cell.RowKey}:columnDef={cell.ColumnDefId}");
                continue;
            }

            if (manual.Contains($"{cell.RowKey}:{column.Code}"))
            {
                kept.Add($"{cell.RowKey}:{column.Code}");
                continue;
            }

            candidates.Add((cell, column));
        }

        var current = await CurrentValuesAsync(periodKey, candidates, existing, ct).ConfigureAwait(false);

        var changed = candidates
            .Where(c => !existing.TryGetValue(c.Cell.RowKey, out var row)
                        || !Unchanged(c.Cell, c.Column, row.Id, periodKey, current))
            .ToList();

        var awaiting = await AwaitingConfirmationAsync(tableInstanceId, periodKey, changed, existing, ct)
            .ConfigureAwait(false);

        var rows = new List<PatchRow>();
        var applied = 0;

        foreach (var group in changed.GroupBy(c => c.Cell.RowKey, StringComparer.Ordinal))
        {
            existing.TryGetValue(group.Key, out var row);

            var patchCells = group
                .Where(c => !awaiting.Contains($"{c.Cell.RowKey}:{c.Column.Code}"))
                .Select(c => new PatchCell(c.Column.Code, c.Cell.Value))
                .ToList();

            if (patchCells.Count == 0)
            {
                continue;
            }

            // ⚠ Наявний рядок — з його версією (оновлення), відсутній — `null`
            // (створення, R-B2): рядок-адресат описаний у шаблоні, і його
            // поява — не конфлікт.
            rows.Add(new PatchRow(group.Key, BaseVersion: row?.RowVersion, patchCells));
            applied += patchCells.Count;
        }

        return new WritePlan(rows, kept, applied, [.. awaiting.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Комірки, запис у які правило періоду дозволяє лише з підтвердженням
    /// людини (<c>ФВ-2.16</c>, <c>AllowWithConfirmation</c>): <c>rowKey:columnCode</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Підтвердження — дія людини, а інтеграція підтверджувати не може і не
    /// повинна: запис у таку комірку від технічного запису означав би
    /// автоматичне підтвердження, якого ніхто не давав. Тому такі комірки НЕ
    /// пишуться, а повертаються окремим переліком — задача кладе їх у журнал
    /// покриття (<c>D-118</c>: «зібрано, але не записано» — не мовчки).
    ///
    /// ⚠ Питаємо ту саму службу доступу з тим самим профілем, яким рішення
    /// ухвалить обробник запису, — не повторюємо правила тут. Решту відмов
    /// (закритий період, поданий аркуш, обчислювана колонка) дає обробник, як
    /// і доти: тут відсіюється лише те, що обробник ДОЗВОЛИВ би без
    /// підтвердження, якого не було.
    ///
    /// ⚠ Нуль запитів, коли писати нічого: сторож області мапінгів
    /// (<c>MaterializeMappingScopeTests</c>) ганяє патчер без служб доступу.
    /// </remarks>
    private async Task<HashSet<string>> AwaitingConfirmationAsync(
        long tableInstanceId,
        PeriodKey periodKey,
        List<(IntegrationCellValue Cell, ColumnDef Column)> changed,
        Dictionary<string, RowState> existing,
        CancellationToken ct)
    {
        var awaiting = new HashSet<string>(StringComparer.Ordinal);

        if (changed.Count == 0)
        {
            return awaiting;
        }

        var userId = currentUser.UserId
                     ?? throw new InvalidOperationException(
                         "Запис інтеграції поза задачею: автора немає (IntegrationActor.EnterAsync не викликано).");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var inExisting = changed
            .Where(c => existing.ContainsKey(c.Cell.RowKey))
            .Select(c => (c.Cell.RowKey, c.Column.Code, Address: new CellAddress(periodKey, existing[c.Cell.RowKey].Id, c.Column.Id)))
            .ToList();

        if (inExisting.Count > 0)
        {
            var decisions = await access
                .CanEditCellsAsync(profile, tableInstanceId, periodKey, [.. inExisting.Select(c => c.Address).Distinct()], ct)
                .ConfigureAwait(false);

            foreach (var (rowKey, code, address) in inExisting)
            {
                if (decisions.TryGetValue(address, out var decision) && decision.RequiresConfirmation)
                {
                    awaiting.Add($"{rowKey}:{code}");
                }
            }
        }

        var inNew = changed.Where(c => !existing.ContainsKey(c.Cell.RowKey)).ToList();

        if (inNew.Count > 0)
        {
            var decisions = await access
                .CanCreateRowsAsync(
                    profile, tableInstanceId, [.. inNew.Select(c => c.Cell.RowKey).Distinct(StringComparer.Ordinal)], ct)
                .ConfigureAwait(false);

            foreach (var (cell, column) in inNew)
            {
                if (decisions.TryGetValue(cell.RowKey, out var row)
                    && row.Columns.TryGetValue(column.Id, out var decision)
                    && decision.RequiresConfirmation)
                {
                    awaiting.Add($"{cell.RowKey}:{column.Code}");
                }
            }
        }

        return awaiting;
    }

    /// <summary>Чинні значення комірок-кандидатів у наявних рядках.</summary>
    private async Task<IReadOnlyDictionary<CellAddress, CellValueData>> CurrentValuesAsync(
        PeriodKey periodKey,
        List<(IntegrationCellValue Cell, ColumnDef Column)> candidates,
        Dictionary<string, RowState> existing,
        CancellationToken ct)
    {
        var addresses = candidates
            .Where(c => existing.ContainsKey(c.Cell.RowKey))
            .Select(c => new CellAddress(periodKey, existing[c.Cell.RowKey].Id, c.Column.Id))
            .Distinct()
            .ToList();

        return addresses.Count == 0
            ? new Dictionary<CellAddress, CellValueData>()
            : await cellStore.ReadCellsAsync(addresses, ct).ConfigureAwait(false);
    }

    /// <summary>Чи збіглося б записане значення з чинним.</summary>
    /// <remarks>
    /// ⚠ Порівняння — тим самим критерієм, яким обробник вирішує «зміни не
    /// було» для журналу (`U-22`): значення, розібране за колонкою
    /// (<see cref="CellValueReader.Read"/>), дорівнює збереженому як запис,
    /// включно з <c>IsCalculated</c> і <c>IsEmpty</c>. Значення, яке колонка не
    /// приймає, — «змінене»: відмову з причиною має дати обробник, а не
    /// мовчазний пропуск тут.
    /// </remarks>
    private static bool Unchanged(
        IntegrationCellValue cell,
        ColumnDef column,
        long rowId,
        PeriodKey periodKey,
        IReadOnlyDictionary<CellAddress, CellValueData> current)
    {
        if (!current.TryGetValue(new CellAddress(periodKey, rowId, column.Id), out var stored))
        {
            return false;
        }

        try
        {
            return CellValueReader.Read(cell.Value, column) == stored;
        }
        catch (EcrException)
        {
            return false;
        }
    }

    /// <summary>Комірки, останню зміну яких зробила людина.</summary>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Сирий запит, а не LINQ: <c>aud.CellChange</c> — незмінний журнал, і
    /// він навмисно НЕ є сутністю EF. Дати йому <c>DbSet</c> означало б
    /// відкрити можливість писати в нього з коду застосунку, а «незмінний
    /// журнал» тримається саме на тому, що такої можливості немає (B01 §6.4).
    ///
    /// ⚠ Береться ОСТАННЯ зміна кожної комірки, а не будь-яка: комірку могли
    /// спершу заповнити руками, а потім свідомо віддати інтеграції.
    /// </remarks>
    private async Task<HashSet<string>> ManualCellsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var manual = new HashSet<string>(StringComparer.Ordinal);

        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(
            db.Database.GetConnectionString());

        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH last_change AS (
                SELECT c.RowKey, c.ColumnDefId, c.Origin,
                       ROW_NUMBER() OVER (PARTITION BY c.RowKey, c.ColumnDefId
                                              ORDER BY c.ChangedAt DESC, c.Id DESC) AS rn
                  FROM aud.CellChange AS c
                  JOIN doc.TableRow  AS r ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId
                 WHERE c.PeriodKey = @period AND r.TableInstanceId = @instance
            )
            SELECT lc.RowKey, cd.Code
              FROM last_change AS lc
              JOIN cfg.ColumnDef AS cd ON cd.Id = lc.ColumnDefId
             WHERE lc.rn = 1 AND lc.Origin = N'UserEdit';
            """;

        command.Parameters.AddWithValue("@period", periodKey.Value);
        command.Parameters.AddWithValue("@instance", tableInstanceId);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            manual.Add($"{reader.GetString(0)}:{reader.GetString(1)}");
        }

        return manual;
    }
}

/// <summary>Журнал покриття збору поверх <c>itg.CollectionCoverage</c>.</summary>
public sealed class CoverageJournal(EcrDbContext db, IClock clock) : ICoverageJournal
{
    /// <inheritdoc />
    public async Task RecordAsync(
        int sourceEntityId, PeriodKey periodKey, string status, string details, CancellationToken ct)
    {
        db.CollectionCoverages.Add(
            CollectionCoverage.Skipped(sourceEntityId, periodKey.Value, status, details, clock.UtcNow));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordManyAsync(IReadOnlyList<CoverageEvent> events, CancellationToken ct)
    {
        if (events.Count == 0)
        {
            return;
        }

        var now = clock.UtcNow;

        foreach (var e in events)
        {
            db.CollectionCoverages.Add(
                CollectionCoverage.Skipped(e.SourceEntityId, e.PeriodKey.Value, e.Status, e.Details, now));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
