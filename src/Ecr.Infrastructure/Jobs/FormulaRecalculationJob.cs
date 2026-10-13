using System.Globalization;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Каскадний перерахунок ФОРМУЛ ШАБЛОНУ після правки комірок.
/// </summary>
/// <remarks>
/// ⛔ Задача нова, і поява її — це виправлення `A7-63`. До цього
/// <c>PatchCellsHandler</c> ставив у чергу <c>IRecalculationJob</c> — задачу
/// МЕТОДОЛОГІЙ — із тілом <c>{TableInstanceId, PeriodKey}</c>, якого та не
/// розуміє: її запит має поля <c>ProjectId</c>, <c>DocumentId</c>,
/// <c>PeriodKey</c>, <c>TriggeredByUserId</c>. Розбір давав нулі, тобто після
/// кожної правки комірки в чергу лягала задача, яка не могла зробити нічого.
///
/// ⚠ Дві задачі з іменем «перерахунок» — не дублювання. Результати формул
/// шаблону лежать у <c>doc.CellValue</c> з <c>IsCalculated = 1</c>, результати
/// методологій — у <c>calc.CalculationResult</c> (<c>D-69</c>). Плутати ці два
/// шляхи не можна, і саме плутанина й сталася.
/// </remarks>
public sealed class FormulaRecalculationJob(
    RecalculationService recalculation,
    EcrDbContext db,
    // WW-03: необовʼязкові лише для тестів, що збирають задачу вручну; DI підставляє обидва.
    CrossPeriodFanOut? fanOut = null,
    IPeriodStore? periods = null) : IFormulaRecalculationJob
{
    /// <summary>Ключ каталогу відмови спільного блокування аркуша (<c>SheetEditGate.Busy</c>).</summary>
    private const string SheetBeingSubmittedKey = "err.ECR-DOC-4091.sheetBeingSubmitted";

    /// <summary>Налаштування розбору завдання; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = Parse(payload);

        var dirty = new DirtySet();
        var periodKey = new PeriodKey(request.PeriodKey);

        // WW-03: елемент `cells` із `TableDefId` — не адреса комірки, а «у іншому періоді змінилась
        // колонка» (постановка `CrossPeriodFanOut`); такі несуть номер періоду-джерела.
        var sources = new List<CrossPeriodSource>();
        var hops = 0;

        foreach (var cell in request.Cells)
        {
            if (cell.TableDefId is { } sourceTable && cell.SourcePeriod is { } sourcePeriod)
            {
                sources.Add(new CrossPeriodSource(sourcePeriod, sourceTable, cell.ColumnDefId));
                hops = Math.Max(hops, cell.Hops ?? 0);
                continue;
            }

            dirty.Add(new CellAddress(periodKey, cell.RowId, cell.ColumnDefId));
        }

        // ⛔ Порожній набір змінених комірок — це не «перерахувати все», а
        // «нема чого рахувати». Повний перерахунок від порожнього списку
        // перетворив би кожну правку на прогін по всьому документу.
        if (dirty.IsEmpty && sources.Count == 0)
        {
            await progress.ReportKeyAsync(100, FormulaRecalcOutcome.NoneKey, ct).ConfigureAwait(false);

            return;
        }

        // ⛔ Лок документа — той самий, що в повного перерахунку
        // (`RecalculationDocumentLock`). Повний читає входи поза транзакцією запису;
        // без лока він, прочитавши вхід ДО цього PATCH, записував своє старіше
        // похідне число ПІСЛЯ нашого свіжого (`FormulaRecalculationDocumentLockTests`).
        // Під локом порядок один: хто б не був першим, останнім пише той, хто
        // читав останній вхід. Лок береться тут, у задачі, а не в транзакції
        // PATCH: там лише постановка в чергу.
        //
        // ⛔ O1 (I2 ФВ-9.8): не чекаємо довгого власника лока, тримаючи слот
        // виконавця. Зайнято довше за `BusyWait` — `JobDeferredException`, і
        // виконавець повертає задачу в чергу через `DeferDelay`, не рахуючи спроби.
        var documentId = request.TableInstanceId != 0
            ? await DocumentOfAsync(request, ct).ConfigureAwait(false)
            : request.DocumentId;

        var busyWait = RecalculationDocumentLock.BusyWait;
        await using var documentLock =
            await RecalculationDocumentLock.AcquireAsync(db, documentId, busyWait, ct).ConfigureAwait(false);

        int written = 0;
        var changed = new HashSet<(int TableDefId, int ColumnDefId)>();
        try
        {
            if (!dirty.IsEmpty)
            {
                written = await recalculation
                    .RecalculateAsync(request.TableInstanceId, dirty, ct, changed)
                    .ConfigureAwait(false);
            }

            // WW-03: період-читач. Закритий період не перераховується ніколи (стан міг змінитись
            // між постановкою й виконанням); подані аркуші відсікає сам запис прогону.
            if (sources.Count > 0 && documentId != 0 && periods is not null
                && await periods.FindPeriodStateAsync(documentId, periodKey.Value, ct).ConfigureAwait(false)
                    is not (null or Ecr.Domain.Enums.PeriodState.Closed))
            {
                written += await recalculation
                    .RecalculateFromOtherPeriodsAsync(documentId, periodKey, sources, ct, changed)
                    .ConfigureAwait(false);
            }
        }
        catch (ConcurrencyConflictException busy) when (IsSheetBeingSubmitted(busy))
        {
            // ⛔ X6-02: аркуш цього прогону подається (чи подання вже в черзі), а прогін
            // інших аркушів у черзі за ним не стоїть (`EnterEditNoWaitAsync`). Це не провал
            // і не ретрай (спроби 30/60/120 с вичерпались би за годину дедлайну): транзакція
            // відкочена, нічого не записано — задача повертається в чергу, не рахуючи спроби.
            throw new JobDeferredException(RecalculationDocumentLock.DeferDelay, busy.Message);
        }
        catch (Exception ex) when (RecalculationDocumentLock.IsLockWaitTimeout(ex))
        {
            // ⛔ F1 (WW-02): вичерпане очікування блокування даних (`ECR-DOC-4091`, `lockTimeout`) — зайнятість, що
            // минає; не ретрай 30/60/120 с із `Failed` і застарілими обчисленими колонками, а відкладення (див.
            // `RecalculationJob`). Транзакція відкочена — нічого не записано.
            throw new JobDeferredException(
                RecalculationDocumentLock.LockWaitDeferDelay(documentId),
                "Document data is busy with a long operation; recalculation deferred.",
                RecalculationDocumentLock.Resource(documentId));
        }

        // WW-03: ланцюжок читачів — зміни цього прогону (насіння й похідні) йдуть в інші періоди року.
        // Постановка coalesced; без змін у записаному далі нічого не ставиться (ланцюжок завершується).
        if (fanOut is not null && documentId != 0 && changed.Count > 0)
        {
            await fanOut
                .EnqueueReadersAsync(documentId, periodKey, changed, onlyEarlierReaders: false, hops + 1, ct)
                .ConfigureAwait(false);
        }

        await progress
            .ReportKeyAsync(
                100,
                FormulaRecalcOutcome.DoneKey,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [FormulaRecalcOutcome.WrittenParam] = written.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Відмова «аркуш подається» (<c>ECR-DOC-4091</c>, ключ <c>sheetBeingSubmitted</c>) — минає сама.</summary>
    /// <remarks>
    /// ⚠ За кодом і ключем каталогу, не за текстом: той самий <c>ECR-DOC-4091</c> несе й
    /// «структуру змінено» (<c>structureChanged</c>) — це вже не черга, а інша версія шаблону.
    /// ⚠ <c>internal</c>: той самий вердикт читає <see cref="RecalculationJob"/> (Y1-02) — одна
    /// класифікація на обидві задачі, не копія.
    /// </remarks>
    internal static bool IsSheetBeingSubmitted(ConcurrencyConflictException ex)
        => ex.ErrorCode == Domain.Errors.ErrorCodes.SheetBusy
           && ex.Details?.GetValueOrDefault("messageKey") is SheetBeingSubmittedKey;

    /// <summary>Документ екземпляра таблиці; <c>0</c> — екземпляра немає.</summary>
    /// <remarks>
    /// ⚠ З ключем періоду: <c>doc.TableInstance</c> партиціонована за ним. Немає
    /// екземпляра — лок не береться, а <see cref="RecalculationService.RecalculateAsync"/>
    /// нижче відмовить власним повідомленням.
    /// </remarks>
    private Task<long> DocumentOfAsync(FormulaRecalculationRequest request, CancellationToken ct)
        => db.TableInstances
            .AsNoTracking()
            .Where(i => i.Id == request.TableInstanceId && i.PeriodKeyValue == request.PeriodKey)
            .Select(i => i.DocumentId)
            .FirstOrDefaultAsync(ct);

    private static FormulaRecalculationRequest Parse(object? payload)
    {
        if (payload is FormulaRecalculationRequest typed)
        {
            return typed;
        }

        var json = payload as string ?? JsonSerializer.Serialize(payload);

        return JsonSerializer.Deserialize<FormulaRecalculationRequest>(json, PayloadOptions)
               ?? throw new InvalidOperationException(
                   "Завдання перерахунку формул не розбирається: невідома форма payload.");
    }
}

/// <summary>Завдання на каскадний перерахунок формул.</summary>
/// <param name="TableInstanceId">Екземпляр таблиці, у якому сталася правка.</param>
/// <param name="PeriodKey">Період правки.</param>
/// <param name="DocumentId">
/// WW-03: документ — коли <paramref name="TableInstanceId"/> = 0 (задача періоду-читача, екземпляра немає).
/// </param>
/// <param name="Cells">Змінені комірки — насіння каскаду.</param>
/// <remarks>
/// ⚠ Змінені комірки передаються ЯВНО, а не виводяться із «щось змінилося».
/// Без них перерахунок був би повним на кожну правку, і сенс графа
/// залежностей зник би разом із його вартістю.
/// </remarks>
public sealed record FormulaRecalculationRequest(
    long TableInstanceId, int PeriodKey, IReadOnlyList<DirtyCell> Cells, long DocumentId = 0);

/// <summary>Змінена комірка в завданні перерахунку.</summary>
/// <param name="RowId">Рядок документа.</param>
/// <param name="ColumnDefId">Колонка.</param>
/// <param name="TableDefId">
/// WW-03: заповнене лише в елементі «у ІНШОМУ періоді змінилась ця колонка таблиці» (<c>RowId = 0</c>).
/// </param>
/// <param name="SourcePeriod">WW-03: номер періоду-джерела в році для такого елемента.</param>
/// <param name="Hops">
/// WW-03: скільки задач-читачів уже пройшов ланцюжок; стеля (<see cref="CrossPeriodFanOut.MaxHops"/>)
/// не дає розбіжному міжперіодному циклу ставити задачі безкінечно.
/// </param>
public sealed record DirtyCell(
    long RowId, int ColumnDefId, int? TableDefId = null, int? SourcePeriod = null, int? Hops = null);
