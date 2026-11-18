using System.Globalization;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
public sealed partial class FormulaRecalculationJob(
    RecalculationService recalculation,
    EcrDbContext db,
    // WW-03: необовʼязкові лише для тестів, що збирають задачу вручну; DI підставляє обидва.
    CrossPeriodFanOut? fanOut = null,
    IPeriodStore? periods = null,
    ILogger<FormulaRecalculationJob>? logger = null,
    // ✎ B79: журнал і годинник — для позначки «перераховано після подання»; без них (ручна збірка в тестах) позначки немає.
    IAuditWriter? audit = null,
    Ecr.Domain.Abstractions.IClock? clock = null,
    // B179 L2: зміна документа-джерела (правка комірки дає лише цю задачу) — перерахунок залежних.
    IRecalculationCompletionHook? completionHook = null) : IFormulaRecalculationJob
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

            var seedInstance = cell.TableInstanceId ?? (cellPeriod == request.PeriodKey ? request.TableInstanceId : 0);
            var seed = new CellAddress(new PeriodKey(cellPeriod), cell.RowId, cell.ColumnDefId);
            item.Dirty.Add(seed);
            item.AddDirty(seedInstance, seed);
            item.InstanceId = item.InstanceId != 0 ? item.InstanceId : seedInstance;

            // ✎ D-442: подані аркуші, правлені погоджувачем поточного кроку (позначка з насіння PATCH): каскад пише їх
            // БЕЗ позначки «перераховано після подання» (це правка самого погоджувача). ✎ B79: решту поданих каскад
            // теж пише, але з позначкою; затверджені пропускає.
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

        // B179 L2: періоди правки (насіння) — вони змінились, навіть коли похідних формул немає (written = 0).
        var seedPeriods = work.Where(p => !p.Value.Dirty.IsEmpty).Select(p => p.Key).ToList();
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

        // ⛔ D-8: відкладені періоди проходу. Повтор задачі виконує ПОЧАТКОВИЙ payload (лише насіння), а читачі, знайдені
        // в цьому прогоні, жили тільки в `work`: на повторі насіннєвий період уже записаний, похідні не змінюються, і
        // читач похідної колонки більше не знаходився — значення лишалось застарілим без жодної помилки.
        var postponed = new List<(int Slot, PeriodWork Item)>();

        // L2: спільні читання проходу — екземпляри/залежності не перечитуються на кожен період і ланку.
        using var shared = recalculation.NewSharedLoad();

        // ⛔ Прохід за зростанням періоду: беремо НАЙМЕНШИЙ зачеплений. Читач РАНІШЕ за джерело (абсолютне
        // `[Period:#k]`, k пізніше) дописується в перелік і береться наступним — як і раніше, коли це була окрема
        // задача. Період, що вже оброблений, може потрапити в перелік знову (його знов зачепила чужа зміна) —
        // тоді він рахується ще раз, а дубль роботи в МЕЖАХ одного набору злиття неможливий (слоти по періоду).
        // Завершення: кожна ланка ланцюжка несе `hops` на 1 більше за ту, що її породила, і `FindReadersAsync`
        // зупиняється за `CrossPeriodFanOut.MaxHops` (розбіжний цикл `x = [Period:#2].x + 1`); `MaxPasses` — запас.
        var passes = 0;
        (int Slot, PeriodWork Item)? current = null;
        try
        {
            while (work.Count > 0 && passes < MaxPasses)
            {
                var (slot, item) = work.First();
                work.Remove(slot);
                current = (slot, item);
                passes++;

                var periodKey = new PeriodKey(slot);
                var changed = new HashSet<(int TableDefId, int ColumnDefId)>();

                // Q-17: колонки ВВЕДЕНИХ комірок (насіння правки) — не похідні: лише вони можуть бути стовпцями з чужим
                // власником, чиї читачі в інших екземплярах року не бачили б зміни.
                var inputColumns = new HashSet<(int TableDefId, int ColumnDefId)>();
                var periodWritten = 0;

                // ✎ B79 (правило людини 07.11: «пропускати лише погоджені; подані — з позначкою»): каскад пише й ПОДАНІ
                // аркуші періоду (затверджені — ніколи). Тут — подані аркуші, у які прогін справді записав (після коміту).
                var submittedWritten = new HashSet<(int SheetDefId, long? CopyId)>();

                // ✎ B79: річна розкладка — подані МІСЯЦІ року, чиє не-місячне число прогін змінив (воно живе в якорі й
                // пишеться за агрегатом року): позначка кожному такому місяцю, а не лише періоду прогону.
                var submittedYearWritten = new HashSet<(int PeriodKey, int SheetDefId)>();
                try
                {
                    if (!item.Dirty.IsEmpty)
                    {
                        // RS: насіння оригіналу й кожної копії рахується окремим «видом» (самопосилання копії читає копію).
                        foreach (var (instanceId, dirty) in await DirtyByScopeAsync(item, documentId, periodKey, shared, ct)
                                     .ConfigureAwait(false))
                        {
                            periodWritten += await recalculation
                                .RecalculateAsync(
                                    instanceId, dirty, ct, changed, item.ApproverSheets, inputColumns, shared,
                                    submittedWritten: submittedWritten, submittedYearWritten: submittedYearWritten)
                                .ConfigureAwait(false);
                        }
                    }

                    // WW-03: період-читач. Закритий період не перераховується ніколи (стан міг змінитись
                    // між постановкою й виконанням); подані аркуші відсікає сам запис прогону.
                    // ✎ LAND-YEAR B58: виняток — закритий ЯКІР річної розкладки у відкритому році: лише не-місячні цілі під
                    // агрегатом року (`CrossPeriodFanOut.ClosedAnchorYearAsync`, правило B24), перевірене тут ще раз.
                    if (item.Sources.Count > 0 && documentId != 0 && periods is not null)
                    {
                        var readerState = await periods.FindPeriodStateAsync(documentId, periodKey.Value, ct).ConfigureAwait(false);
                        IReadOnlyList<int>? annualPeriods = null;
                        if (readerState == Ecr.Domain.Enums.PeriodState.Closed && fanOut is not null)
                        {
                            annualPeriods = await fanOut.ClosedAnchorYearAsync(documentId, periodKey, ct, shared).ConfigureAwait(false);
                        }

                        if (readerState is not (null or Ecr.Domain.Enums.PeriodState.Closed) || annualPeriods is not null)
                        {
                            periodWritten += await recalculation
                                .RecalculateFromOtherPeriodsAsync(
                                    documentId, periodKey, item.Sources, ct, changed, shared, annualPeriods,
                                    submittedWritten: submittedWritten, submittedYearWritten: submittedYearWritten)
                                .ConfigureAwait(false);
                        }
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
                    postponed.Add((slot, item));
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
                    postponed.Add((slot, item));
                    db.ChangeTracker.Clear();
                    continue;
                }

                written += periodWritten;
                if (periodWritten > 0)
                {
                    writtenPeriods.Add(slot);
                }

                // ✎ B79: поданий аркуш, у який каскад записав, — запис журналу й позначка «перераховано після подання»
                // (той самий механізм, що B63/B83: `SheetRecalc/RecalculatedAfterSubmission`, читає картка документа).
                // Подані місяці року (не-місячне число) — тим самим записом, за своїм періодом; місяць самого прогону, уже
                // названий у `submittedWritten`, удруге не пишеться.
                var marks = submittedYearWritten
                    .GroupBy(m => m.PeriodKey)
                    .ToDictionary(g => g.Key, g => g.Select(m => (m.SheetDefId, CopyId: (long?)null)).ToHashSet());
                if (submittedWritten.Count > 0)
                {
                    if (marks.TryGetValue(slot, out var own))
                    {
                        own.UnionWith(submittedWritten);
                    }
                    else
                    {
                        marks[slot] = submittedWritten;
                    }
                }

                foreach (var (markPeriod, markSheets) in marks.OrderBy(m => m.Key))
                {
                    await SubmittedSheetRecalcLog
                        .WriteAsync(db, audit, clock, documentId, markPeriod, markSheets, ct)
                        .ConfigureAwait(false);
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            // ⛔ D-8: непередбачений виняток посеред проходу. Ранні періоди вже закомічені, а ретрай виконає ПОЧАТКОВИЙ
            // payload (лише насіння) і не знайде читачів похідних колонок — тож поточний, відкладені й ще не оброблені
            // періоди-читачі ставляться окремою постановкою, і лише потім виняток іде далі (скасування не ловимо).
            db.ChangeTracker.Clear();
            IEnumerable<(int Slot, PeriodWork Item)> pending = postponed.Concat(work.Select(p => (p.Key, p.Value)));
            if (current is { } failed && failed.Item.Sources.Count > 0)
            {
                // ⛔ Збій у обробці САМОГО періоду-читача може бути детермінованим («отруйний» вхід): нова задача впала б
                // тут же й поставила б себе знову без кінця (ліміт спроб діє на кожну задачу окремо). Тому ланка несе
                // Hops + 1, а за `CrossPeriodFanOut.MaxHops` ланцюжок обривається.
                failed.Item.Hops++;
                if (failed.Item.Hops <= CrossPeriodFanOut.MaxHops)
                {
                    pending = pending.Append(failed);
                }
            }

            try
            {
                await RequeueSourcesAsync(documentId, pending, ct).ConfigureAwait(false);
            }
            catch (Exception requeueFailure) when (requeueFailure is not OperationCanceledException)
            {
                // Збій самої постановки не підміняє першопричину: ретрай і так піде за початковим винятком.
                if (logger is not null)
                {
                    LogRequeueFailed(logger, requeueFailure, documentId);
                }
            }

            throw;
        }

        if (deferral is not null)
        {
            // ⛔ D-8: повтор отримає ЛИШЕ початковий payload; джерела відкладених періодів-читачів — окремою постановкою
            // (злиттям на ту саму ціль документа, тож дублікати зливаються й з самою відкладеною задачею).
            await RequeueSourcesAsync(documentId, postponed, ct).ConfigureAwait(false);
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

        // B179 L2 (Q1): УСПІШНИЙ прохід — документ-джерело змінився в періодах насіння й записаних; хук сам вирішує, чи
        // документ — джерело зв'язки за Any. Відкладений чи провалений прохід сюди не доходить. Ланка — 0: правка
        // людини (чи читача іншого періоду) починає ланцюжок заново.
        // ✎ B179 L2b: прохід із НАСІННЯМ (правка комірок) — `OnCellsEditedAsync`: хук ставить власний перерахунок методологій
        // джерела, а споживачів — уже його успіх. Прохід лише з міжперіодних джерел (його ставить `CrossPeriodFanOut`, зокрема
        // сам `RecalculationJob`) — як у L2: власного перерахунку не ставить, тож петлі «перерахунок → формули → перерахунок» немає.
        if (completionHook is not null && documentId != 0)
        {
            var changedPeriods = writtenPeriods.Union(seedPeriods).Order().ToArray();
            if (changedPeriods.Length > 0)
            {
                var changed = new Dictionary<long, int[]> { [documentId] = changedPeriods };
                await (seedPeriods.Count > 0
                        ? completionHook.OnCellsEditedAsync(changed, ct)
                        : completionHook.OnSucceededAsync(changed, sourceHop: 0, ct))
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// RS: насіння періоду, розкладене за «видами» — оригінал і кожна копія аркуша (екземпляр-представник + його насіння).
    /// </summary>
    /// <remarks>
    /// ⚠ Звичайний випадок (насіння одного екземпляра або копій у документі немає) — рівно один виклик, як і доти.
    /// Екземпляр без відомого Id (<c>0</c>) відноситься до оригіналу.
    /// </remarks>
    private static async Task<IReadOnlyList<(long InstanceId, DirtySet Dirty)>> DirtyByScopeAsync(
        PeriodWork item, long documentId, PeriodKey periodKey, RecalculationSharedLoad shared, CancellationToken ct)
    {
        if (item.DirtyByInstance.Count <= 1 && item.InstanceId != 0)
        {
            return [(item.InstanceId, item.Dirty)];
        }

        var instances = await shared.InstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);

        if (item.DirtyByInstance.Count <= 1 || instances.All(i => i.SheetCopyId is null))
        {
            var only = item.InstanceId != 0
                ? item.InstanceId
                : instances.Select(i => i.TableInstanceId).FirstOrDefault();

            return only == 0 ? [] : [(only, item.Dirty)];
        }

        var copyOf = instances.ToDictionary(i => i.TableInstanceId, i => i.SheetCopyId);
        var scopes = new SortedDictionary<long, (long InstanceId, DirtySet Dirty)>();
        foreach (var (seedInstance, seeds) in item.DirtyByInstance)
        {
            var copyId = copyOf.GetValueOrDefault(seedInstance);
            if (!scopes.TryGetValue(copyId ?? 0, out var scope))
            {
                var representative = seedInstance != 0
                    ? seedInstance
                    : instances.Where(i => i.SheetCopyId is null).Select(i => i.TableInstanceId).FirstOrDefault();
                scopes[copyId ?? 0] = scope = (representative, new DirtySet());
            }

            foreach (var seed in seeds.Seeds)
            {
                scope.Dirty.Add(seed);
            }
        }

        return [.. scopes.Values.Where(s => s.InstanceId != 0)];
    }

    /// <summary>D-8: ставить у чергу періоди-читачі відкладеної задачі разом із уже знайденими джерелами.</summary>
    private async Task RequeueSourcesAsync(
        long documentId, IEnumerable<(int Slot, PeriodWork Item)> pending, CancellationToken ct)
    {
        if (fanOut is null || documentId == 0)
        {
            return;
        }

        foreach (var (slot, item) in pending.Where(p => p.Item.Sources.Count > 0))
        {
            await fanOut.EnqueueSourcesAsync(documentId, new PeriodKey(slot), item.Sources, item.Hops, ct)
                .ConfigureAwait(false);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "formula-recalc: не вдалося поставити в чергу читачів після збою проходу, документ {DocumentId}.")]
    private static partial void LogRequeueFailed(ILogger logger, Exception exception, long documentId);

    /// <summary>Стеля проходів одного запуску: (ланки ланцюжка + 1) × періоди року + запас; страховка від циклу.</summary>
    internal const int MaxPasses = (CrossPeriodFanOut.MaxHops + 1) * 13;

    /// <summary>Робота над одним періодом у проході: насіння, джерела змін з інших періодів, глибина ланцюжка.</summary>
    private sealed class PeriodWork
    {
        private readonly HashSet<CrossPeriodSource> _known = [];

        public DirtySet Dirty { get; } = new();

        /// <summary>RS: те саме насіння за екземпляром комірки (<c>0</c> — екземпляр невідомий).</summary>
        public Dictionary<long, DirtySet> DirtyByInstance { get; } = [];

        public void AddDirty(long instanceId, CellAddress address)
        {
            if (!DirtyByInstance.TryGetValue(instanceId, out var set))
            {
                DirtyByInstance[instanceId] = set = new DirtySet();
            }

            set.Add(address);
        }

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
