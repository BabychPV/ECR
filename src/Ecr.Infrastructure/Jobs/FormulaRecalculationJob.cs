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

        // L2: задача одна на ДОКУМЕНТ — перелік зачеплених періодів. Кожен елемент `cells` несе свій період
        // (`PeriodKey`), а без позначки — період тіла (старий формат у черзі й прямі виклики).
        var work = new SortedDictionary<int, PeriodWork>();
        foreach (var cell in request.Cells)
        {
            var cellPeriod = cell.PeriodKey ?? request.PeriodKey;
            if (!work.TryGetValue(cellPeriod, out var item))
            {
                work[cellPeriod] = item = new PeriodWork();
            }

            // WW-03: елемент `cells` із `TableDefId` — не адреса комірки, а «у іншому періоді змінилась
            // колонка» (постановка `CrossPeriodFanOut`); такі несуть номер періоду-джерела.
            if (cell.TableDefId is { } sourceTable && cell.SourcePeriod is { } sourcePeriod)
            {
                item.AddSource(new CrossPeriodSource(sourcePeriod, sourceTable, cell.ColumnDefId, cell.OwnerInput == true));
                item.Hops = Math.Max(item.Hops, cell.Hops ?? 0);
                continue;
            }

            item.Dirty.Add(new CellAddress(new PeriodKey(cellPeriod), cell.RowId, cell.ColumnDefId));
            item.InstanceId = item.InstanceId != 0
                ? item.InstanceId
                : cell.TableInstanceId ?? (cellPeriod == request.PeriodKey ? request.TableInstanceId : 0);

            // ✎ D-442: подані аркуші, правлені погоджувачем поточного кроку (позначка з насіння PATCH). Лише їх
            // каскад пише попри `Submitted`; решту поданих/затверджених пропускає, як і доти.
            if (cell.ApproverSheetDefId is { } approverSheet)
            {
                item.ApproverSheets.Add(approverSheet);
            }
        }

        // ⛔ Порожній набір змінених комірок — це не «перерахувати все», а
        // «нема чого рахувати». Повний перерахунок від порожнього списку
        // перетворив би кожну правку на прогін по всьому документу.
        work = new SortedDictionary<int, PeriodWork>(
            work.Where(p => !p.Value.IsEmpty).ToDictionary(p => p.Key, p => p.Value));
        if (work.Count == 0)
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
        var writtenPeriods = new SortedSet<int>();
        JobDeferredException? deferral = null;

        // L2: спільні читання проходу — екземпляри/залежності не перечитуються на кожен період і ланку.
        var shared = recalculation.NewSharedLoad();

        // ⛔ Прохід за зростанням періоду: беремо НАЙМЕНШИЙ зачеплений. Читач РАНІШЕ за джерело (абсолютне
        // `[Period:#k]`, k пізніше) дописується в перелік і береться наступним — як і раніше, коли це була окрема
        // задача. Період, що вже оброблений, може потрапити в перелік знову (його знов зачепила чужа зміна) —
        // тоді він рахується ще раз, а дубль роботи в МЕЖАХ одного набору злиття неможливий (слоти по періоду).
        // Завершення: кожна ланка ланцюжка несе `hops` на 1 більше за ту, що її породила, і `FindReadersAsync`
        // зупиняється за `CrossPeriodFanOut.MaxHops` (розбіжний цикл `x = [Period:#2].x + 1`); `MaxPasses` — запас.
        var passes = 0;
        while (work.Count > 0 && passes < MaxPasses)
        {
            var (slot, item) = work.First();
            work.Remove(slot);
            passes++;

            var periodKey = new PeriodKey(slot);
            var changed = new HashSet<(int TableDefId, int ColumnDefId)>();

            // Q-17: колонки ВВЕДЕНИХ комірок (насіння правки) — не похідні: лише вони можуть бути стовпцями з чужим
            // власником, чиї читачі в інших екземплярах року не бачили б зміни.
            var inputColumns = new HashSet<(int TableDefId, int ColumnDefId)>();
            var periodWritten = 0;
            try
            {
                if (!item.Dirty.IsEmpty)
                {
                    var instanceId = item.InstanceId;
                    if (instanceId == 0)
                    {
                        instanceId = (await shared.InstancesAsync(documentId, periodKey, ct).ConfigureAwait(false))
                            .Select(i => i.TableInstanceId).FirstOrDefault();
                    }

                    if (instanceId != 0)
                    {
                        periodWritten = await recalculation
                            .RecalculateAsync(
                                instanceId, item.Dirty, ct, changed, item.ApproverSheets, inputColumns, shared)
                            .ConfigureAwait(false);
                    }
                }

                // WW-03: період-читач. Закритий період не перераховується ніколи (стан міг змінитись
                // між постановкою й виконанням); подані аркуші відсікає сам запис прогону.
                if (item.Sources.Count > 0 && documentId != 0 && periods is not null
                    && await periods.FindPeriodStateAsync(documentId, periodKey.Value, ct).ConfigureAwait(false)
                        is not (null or Ecr.Domain.Enums.PeriodState.Closed))
                {
                    periodWritten += await recalculation
                        .RecalculateFromOtherPeriodsAsync(documentId, periodKey, item.Sources, ct, changed, shared)
                        .ConfigureAwait(false);
                }
            }
            catch (ConcurrencyConflictException busy) when (IsSheetBeingSubmitted(busy))
            {
                // ⛔ X6-02: аркуш цього прогону подається (чи подання вже в черзі), а прогін
                // інших аркушів у черзі за ним не стоїть (`EnterEditNoWaitAsync`). Це не провал
                // і не ретрай (спроби 30/60/120 с вичерпались би за годину дедлайну): транзакція
                // відкочена, нічого не записано — задача повертається в чергу, не рахуючи спроби.
                // L2: інші періоди проходу це не зупиняє — вони дораховуються, а задача відкладається в кінці
                // (повторний прохід по вже порахованих нічого не пише).
                deferral ??= new JobDeferredException(RecalculationDocumentLock.DeferDelay, busy.Message);
                db.ChangeTracker.Clear();
                continue;
            }
            catch (Exception ex) when (RecalculationDocumentLock.IsLockWaitTimeout(ex))
            {
                // ⛔ F1 (WW-02): вичерпане очікування блокування даних (`ECR-DOC-4091`, `lockTimeout`) — зайнятість, що
                // минає; не ретрай 30/60/120 с із `Failed` і застарілими обчисленими колонками, а відкладення (див.
                // `RecalculationJob`). Транзакція відкочена — нічого не записано.
                deferral ??= new JobDeferredException(
                    RecalculationDocumentLock.LockWaitDeferDelay(documentId),
                    "Document data is busy with a long operation; recalculation deferred.",
                    RecalculationDocumentLock.Resource(documentId));
                db.ChangeTracker.Clear();
                continue;
            }

            written += periodWritten;
            if (periodWritten > 0)
            {
                writtenPeriods.Add(slot);
            }

            // WW-03: ланцюжок читачів — зміни цього прогону (насіння й похідні) йдуть в інші періоди року.
            // L2: читачі дописуються в ЦЕЙ перелік (а не ставляться окремими задачами); без змін у записаному
            // далі нічого не додається (ланцюжок завершується).
            if (fanOut is not null && documentId != 0 && changed.Count > 0)
            {
                var readers = await fanOut
                    .FindReadersAsync(
                        documentId, periodKey, changed, onlyEarlierReaders: false, item.Hops + 1, ct, inputColumns, shared)
                    .ConfigureAwait(false);

                foreach (var (reader, readerSources) in readers)
                {
                    if (!work.TryGetValue(reader.Value, out var next))
                    {
                        work[reader.Value] = next = new PeriodWork();
                    }

                    foreach (var readerSource in readerSources)
                    {
                        next.AddSource(readerSource);
                    }

                    next.Hops = Math.Max(next.Hops, item.Hops + 1);
                }
            }
        }

        if (deferral is not null)
        {
            throw deferral;
        }

        await progress
            .ReportKeyAsync(
                100,
                FormulaRecalcOutcome.DoneKey,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [FormulaRecalcOutcome.WrittenParam] = written.ToString(CultureInfo.InvariantCulture),
                    [FormulaRecalcOutcome.PeriodsParam] =
                        string.Join(',', writtenPeriods.Select(p => p.ToString(CultureInfo.InvariantCulture))),
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Стеля проходів одного запуску: (ланки ланцюжка + 1) × періоди року + запас; страховка від циклу.</summary>
    internal const int MaxPasses = (CrossPeriodFanOut.MaxHops + 1) * 13;

    /// <summary>Робота над одним періодом у проході: насіння, джерела змін з інших періодів, глибина ланцюжка.</summary>
    private sealed class PeriodWork
    {
        private readonly HashSet<CrossPeriodSource> _known = [];

        public DirtySet Dirty { get; } = new();

        public List<CrossPeriodSource> Sources { get; } = [];

        public HashSet<int> ApproverSheets { get; } = [];

        public long InstanceId { get; set; }

        public int Hops { get; set; }

        public bool IsEmpty => Dirty.IsEmpty && Sources.Count == 0;

        public void AddSource(CrossPeriodSource source)
        {
            if (_known.Add(source))
            {
                Sources.Add(source);
            }
        }
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
/// <param name="ApproverSheetDefId">
/// ✎ D-442: аркуш, на якому цю комірку змінив погоджувач поточного кроку (<c>RecalculationSeed</c>); лише його,
/// поданий, прогін має право перерахувати. <c>null</c> — звичайна правка.
/// </param>
/// <param name="OwnerInput">
/// Q-17: у елементі «у іншому періоді змінилась колонка» (<c>TableDefId</c> заповнене) змінено ВВЕДЕНИЙ стовпець
/// таблиці з маршрутизацією власності: читачі — формули з залежністю-коміркою зі зсувом 0, а не лише міжперіодні.
/// </param>
/// <param name="PeriodKey">
/// L2: період, якого стосується елемент (адреса комірки чи період-ЧИТАЧ для «джерела зміни»). Задача формул одна на
/// документ, тож період несе кожен елемент; <c>null</c> — період тіла задачі (старий формат у черзі).
/// </param>
/// <param name="TableInstanceId">L2: екземпляр таблиці комірки-насіння; <c>null</c> — екземпляр тіла задачі.</param>
public sealed record DirtyCell(
    long RowId, int ColumnDefId, int? TableDefId = null, int? SourcePeriod = null, int? Hops = null,
    int? ApproverSheetDefId = null, bool? OwnerInput = null, int? PeriodKey = null, long? TableInstanceId = null);
