using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICalculationResultStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// Пише **тільки** в <c>calc.CalculationResult</c>, <c>calc.CalculationStep</c> і (з HSE301
/// A3b) <c>calc.CalculationInput</c>.
/// У <c>doc.CellValue</c> результати методологій не потрапляють ніколи (D-69).
/// </remarks>
public sealed class CalculationResultStore(EcrDbContext db, IClock clock) : ICalculationResultStore
{
    /// <summary>
    /// Резервує діапазон ідентифікаторів із <c>calc.CalculationResultSeq</c>
    /// одним викликом <c>sp_sequence_get_range</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Діапазон береться ОДНИМ викликом на весь пакет, а не по одному
    /// значенню: <c>Id</c> потрібні ДО вставки, щоб завантажити результати і
    /// трейс одним проходом. Звернення до послідовності на кожен рядок
    /// коштувало б мільйонів round-trip на річному перерахунку.
    /// <para>
    /// ⛔ Директива №11, T10 #50: був публічним членом <c>ICalculationResultStore</c>
    /// без жодного зовнішнього викликача через порт (тільки внутрішній
    /// виклик із <see cref="WriteResultsAsync"/>) — той самий шаблон, що й
    /// приватний <see cref="ReserveStepIdRangeAsync"/> поруч. Прибрано з порту, а не
    /// видалено: логіка жива й потрібна, просто ніхто, крім цього класу, її
    /// не викликає.
    /// </para>
    /// </remarks>
    private async Task<long> ReserveResultIdRangeAsync(int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // sp_sequence_get_range недоступний поза SQL Server, тому провайдер
        // визначає спосіб. Обидва дають безперервний діапазон, і саме це
        // важливо.
        //
        // ⚠ «У тестах — SQLite» звідси прибрано: другого провайдера в дереві
        // немає, тести з базою йдуть на справжньому SQL Server. Гілка нижче —
        // запас на провайдера без послідовностей, а не описання чинного
        // прогону.
        if (!db.Database.IsSqlServer())
        {
            var last = await db.CalculationResults
                .AsNoTracking()
                .Select(r => (long?)r.Id)
                .MaxAsync(ct)
                .ConfigureAwait(false);

            return (last ?? 0) + 1;
        }

        return await SequenceRangeAsync(count, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Резервує <paramref name="size"/> послідовних значень із
    /// <c>calc.CalculationResultSeq</c> і повертає перше.
    /// </summary>
    /// <remarks>
    /// Резервування атомарне й поза транзакцією викликача: два паралельні
    /// записувачі ніколи не отримають спільного значення, хоч би коли кожен із
    /// них потім закомітив.
    /// </remarks>
    private async Task<long> SequenceRangeAsync(long size, CancellationToken ct)
    {
        var range = await db.Database
            .SqlQuery<long>($"""
                DECLARE @first sql_variant;
                EXEC sys.sp_sequence_get_range
                    @sequence_name = N'calc.CalculationResultSeq',
                    @range_size = {size},
                    @range_first_value = @first OUTPUT;
                SELECT CONVERT(bigint, @first) AS Value;
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return range[0];
    }

    /// <inheritdoc />
    public async Task WriteResultsAsync(
        long calculationRunId, IReadOnlyList<CalculationOutput> outputs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        var values = outputs.SelectMany(o => o.Values.Select(v => (Output: o, Value: v))).ToList();
        if (values.Count == 0)
        {
            return;
        }

        var run = await db.CalculationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == calculationRunId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Прогону {calculationRunId} не існує.");

        var nextId = await ReserveResultIdRangeAsync(values.Count, ct).ConfigureAwait(false);

        // ⚠ PeriodKey прогону, а не рядка: результат живе в партиції свого
        // періоду, і повний рік розкладається по дванадцятьох партиціях
        // окремими прогонами.
        var periodKey = run.PeriodKey ?? 0;

        foreach (var (output, value) in values)
        {
            var result = new CalculationResult(
                calculationRunId,
                value.MethodologyVersionId,
                periodKey,
                output.DocumentId,
                output.SourceRowKey,
                value.OutputCode,
                value.Value,
                value.UnitId,

                // ⛔ HSE301 A3a (D-175): вид іде з модуля як є. Без нього проміжне
                // значення лягло б як вихід і потрапило б у зріз `rpt.*`.
                value.Kind);

            typeof(Domain.Abstractions.Entity<long>)
                .GetProperty(nameof(Domain.Abstractions.Entity<long>.Id))!
                .SetValue(result, nextId++);

            result.SetSubstance(value.SubstanceEntryId);
            db.CalculationResults.Add(result);
        }
    }

    /// <inheritdoc />
    public async Task WriteTraceAsync(
        long calculationRunId, IReadOnlyList<CalculationOutput> outputs,
        TraceLevel traceLevel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        // ⛔ Off не пише нічого. Керуємо тим, ЩО пишемо, а не скільки
        // зберігаємо: політика — «нічого не затирається» (ЗБР-1, ЗБР-3).
        if (traceLevel == TraceLevel.Off)
        {
            return;
        }

        var steps = outputs.SelectMany(o => o.Trace.Select(s => (Output: o, Step: s))).ToList();
        var inputs = outputs
            .SelectMany(o => (o.Inputs ?? [])
                .DistinctBy(a => a.ArgumentCode, StringComparer.OrdinalIgnoreCase)
                .Select(a => (Output: o, Argument: a)))
            .ToList();

        if (steps.Count == 0 && inputs.Count == 0)
        {
            return;
        }

        var run = await db.CalculationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == calculationRunId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Прогону {calculationRunId} не існує.");

        var periodKey = run.PeriodKey ?? 0;

        if (steps.Count > 0)
        {
            await WriteStepsAsync(calculationRunId, periodKey, steps, ct).ConfigureAwait(false);
        }

        if (inputs.Count > 0)
        {
            await WriteInputsAsync(calculationRunId, periodKey, inputs, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Кроки трейсу з адресою й результатом.</summary>
    private async Task WriteStepsAsync(
        long calculationRunId,
        int periodKey,
        List<(CalculationOutput Output, CalculationTraceStep Step)> steps,
        CancellationToken ct)
    {
        var nextId = await ReserveStepIdRangeAsync(steps.Count, ct).ConfigureAwait(false);
        var results = PendingResults(calculationRunId);

        foreach (var (output, step) in steps)
        {
            var entity = new CalculationStep(calculationRunId, periodKey, step.StepOrder, step.StepCode);
            typeof(Domain.Abstractions.Entity<long>)
                .GetProperty(nameof(Domain.Abstractions.Entity<long>.Id))!
                .SetValue(entity, nextId++);

            // ⛔ HSE301 A3b (Д-5): адреса кроку — документ, рядок і речовина, як у його
            // результату. Без неї трейс знаходили лише за прогоном, тобто ніяк із комірки.
            entity.SetAddress(output.DocumentId, output.SourceRowKey, step.SubstanceEntryId);

            // У колонку — `TraceJson` v1 (§7.2), коли модуль його дав; інакше голий код
            // помилки, як до кроку.
            entity.Describe(
                step.Expression, step.Value, step.Detail ?? step.TraceJson, ResultIdOf(results, output, step), step.Masked);
            db.CalculationSteps.Add(entity);
        }
    }

    /// <summary>
    /// Входи рядків — <c>calc.CalculationInput</c>: «з яких чисел вийшло це число»
    /// (HSE301 A3b, ФВ-9.13, дефект Д-5).
    /// </summary>
    /// <remarks>
    /// ⛔ До кроку таблицю лише видаляли (<c>DocumentDeletionStore</c>), а не писали ніде:
    /// перерахунок через рік дав би інше число без жодного способу з'ясувати, чому.
    /// Значення — в одиниці ДЖЕРЕЛА, як його бачила формула (ФВ-16.10).
    /// <para>
    /// ⚠ <c>Lookup</c>-аргумент версії <c>Strict</c> формула читала як id запису
    /// (<c>EntryId</c>, D-161) — тож і вхід зберігає id: саме з ним звірятиметься поточна
    /// комірка (§7.3, «змінилося після розрахунку»).
    /// </para>
    /// </remarks>
    private async Task WriteInputsAsync(
        long calculationRunId,
        int periodKey,
        List<(CalculationOutput Output, CalculationArgument Argument)> inputs,
        CancellationToken ct)
    {
        var nextId = await ReserveInputIdRangeAsync(inputs.Count, ct).ConfigureAwait(false);

        foreach (var (output, argument) in inputs)
        {
            var row = new CalculationInputRow(
                calculationRunId, periodKey, output.DocumentId, output.SourceRowKey, argument.ArgumentCode);
            typeof(Domain.Abstractions.Entity<long>)
                .GetProperty(nameof(Domain.Abstractions.Entity<long>.Id))!
                .SetValue(row, nextId++);

            // ⚠ Колонка — nvarchar(400). Довший текст обрізається тут, а не валить
            // SaveChanges: у тому самому наборі змін — результати прогону, і вхід-пояснення
            // не має права забрати їх із собою.
            var text = argument.ValueString is { Length: > MaxInputText } full
                ? full[..MaxInputText]
                : argument.ValueString;

            row.SetValue(argument.Value ?? argument.EntryId, text, argument.UnitId);
            db.CalculationInputs.Add(row);
        }
    }

    /// <summary>Довжина <c>calc.CalculationInput.ValueString</c>.</summary>
    private const int MaxInputText = 400;

    /// <summary>
    /// Резервує безперервний діапазон ідентифікаторів входів і повертає перший.
    /// </summary>
    /// <remarks>
    /// Та сама спільна <c>calc.CalculationResultSeq</c>, що й для кроків, і з тієї самої
    /// причини: гілки пакета пишуть паралельно, кожна своїм контекстом, а власної
    /// послідовності в таблиці немає (нова — це міграція). Ключ —
    /// <c>(PeriodKey, Id)</c> у своїй таблиці, перетин значень з іншими таблицями нічого
    /// не ламає. Старих Id з <c>MAX+1</c> тут не буває: до кроку A3b таблицю не писав ніхто.
    /// </remarks>
    private async Task<long> ReserveInputIdRangeAsync(int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        // Запас на провайдера без послідовностей — як у ReserveResultIdRangeAsync.
        if (!db.Database.IsSqlServer())
        {
            var last = await db.CalculationInputs
                .AsNoTracking()
                .Select(i => (long?)i.Id)
                .MaxAsync(ct)
                .ConfigureAwait(false);

            return (last ?? 0) + 1;
        }

        return await SequenceRangeAsync(count, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Результати прогону, які <see cref="WriteResultsAsync"/> уже поставив у цей контекст:
    /// ключ результату → його Id.
    /// </summary>
    /// <remarks>
    /// ⚠ Id результату відомий ДО <c>SaveChanges</c> — його резервує послідовність, — тому
    /// крок прив'язується в тому самому наборі змін, без повторного читання бази.
    /// <c>CalculationOutputWriter</c> кличе запис результатів раніше за трейс саме для цього.
    /// </remarks>
    private Dictionary<(long, string?, int, string, long?), long> PendingResults(long calculationRunId)
    {
        var found = new Dictionary<(long, string?, int, string, long?), long>();

        foreach (var entry in db.ChangeTracker.Entries<CalculationResult>())
        {
            var result = entry.Entity;
            if (result.CalculationRunId == calculationRunId)
            {
                found.TryAdd(
                    (result.DocumentId, result.SourceRowKey, result.MethodologyVersionId,
                     result.OutputCode.ToUpperInvariant(), result.SubstanceEntryId),
                    result.Id);
            }
        }

        return found;
    }

    /// <summary>Результат, який дав крок: той самий рядок, код і речовина.</summary>
    /// <remarks>
    /// Версію методології крок не несе — її дає значення рядка з тим самим кодом і
    /// речовиною. Кроку без такого значення (проміжна невидима формула, вихід, що не
    /// порахувався) результату немає, і <c>ResultId</c> лишається <c>null</c>.
    /// </remarks>
    private static long? ResultIdOf(
        Dictionary<(long, string?, int, string, long?), long> results,
        CalculationOutput output,
        CalculationTraceStep step)
    {
        var value = output.Values.FirstOrDefault(v =>
            string.Equals(v.OutputCode, step.StepCode, StringComparison.OrdinalIgnoreCase)
            && v.SubstanceEntryId == step.SubstanceEntryId);

        return value is not null
               && results.TryGetValue(
                   (output.DocumentId, output.SourceRowKey, value.MethodologyVersionId,
                    value.OutputCode.ToUpperInvariant(), value.SubstanceEntryId),
                   out var id)
            ? id
            : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Зрізи <c>rpt.*</c> будуються Етапом 5; поки їх немає, інвалідувати
    /// нічого. Метод існує, щоб послідовність завершення прогону була повною
    /// вже зараз — інакше на Етапі 5 довелося б згадати про неї самому.
    /// </remarks>
    public Task InvalidateReportSnapshotsAsync(long calculationRunId, CancellationToken ct)
        => Task.CompletedTask;

    /// <inheritdoc />
    public async Task SwitchCurrentRunAsync(
        long calculationRunId, string modulesProfileJson, CancellationToken ct)
    {
        var run = await db.CalculationRuns
            .FirstOrDefaultAsync(r => r.Id == calculationRunId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Прогону {calculationRunId} не існує.");

        // ⚠ Обидві половини — в одному наборі змін, який коміт застосує разом
        // (ФВ-9.11). Між зняттям актуальності зі старого прогону і
        // встановленням новому існує стан, у якому актуальних прогонів нуль
        // або два; звіт, побудований у цю мить, не має правильної відповіді.
        //
        // ⛔ Одного цього НЕ ДОСИТЬ, і попередня версія помилялася саме тут:
        // «одна транзакція» захищає лише від напівзастосованого перемикання
        // ВСЕРЕДИНІ одного виклику. Вибірка нижче бачить ЗНІМОК, прочитаний на
        // початку цієї транзакції, тож два одночасні виклики для того самого
        // проєкту й періоду одне одного не бачать, обидва нікого не знімають з
        // актуальності — і комітяться обидва. Інваріант тримає БАЗА
        // (`UX_CalculationRun_Current`): переможений гонитви падає на
        // унікальному індексі замість того, щоб мовчки додати другий
        // актуальний прогін, з якого `ReadCurrentAsync` зібрала б кожне число
        // документа двічі.
        var previousQuery = db.CalculationRuns
            .Where(r => r.ProjectId == run.ProjectId
                        && r.PeriodKey == run.PeriodKey
                        && r.Id != calculationRunId
                        && r.Status == CalculationRun.CurrentStatus);

        // ⛔ «CalculationRun ховає результати сусідніх документів» (третя
        // хвиля UX-PASS R4). Прогін ОДНОГО документа (`run.DocumentId`
        // задано) знімає актуальність ЛИШЕ з прогонів ТОГО САМОГО документа —
        // не з прогону всього проєкту й не з прогонів інших документів: він
        // порахував наново тільки свій документ, тож чужа актуальність
        // лишається чинною. Прогін УСЬОГО проєкту (`run.DocumentId == null`)
        // і далі знімає актуальність з УСІХ прогонів області, як і раніше —
        // він рахує кожен документ проєкту заново, тож усе попереднє (і
        // проєктне, і документне) застаріло разом.
        if (run.DocumentId is { } documentId)
        {
            previousQuery = previousQuery.Where(r => r.DocumentId == documentId);
        }

        var previous = await previousQuery
            .OrderBy(r => r.Id)
            .Take(MaxSupersededRuns)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var stale in previous)
        {
            stale.Supersede();
        }

        run.Complete(CalculationRun.CurrentStatus, clock.UtcNow, modulesProfileJson, errorMessage: null);
        run.MakeCurrent();
    }

    /// <summary>
    /// Стеля на кількість прогонів, з яких знімається актуальність.
    /// </summary>
    /// <remarks>
    /// Актуальний прогін мусить бути рівно один; більший список означає
    /// зіпсовані дані. Межа тут не оптимізація, а те, що не дає такій
    /// зіпсованості перетворитися на довгу транзакцію.
    /// </remarks>
    private const int MaxSupersededRuns = 100;

    /// <summary>Стеля вибірки результатів на один документ і період.</summary>
    /// <remarks>
    /// Рядків стільки, скільки виходів × речовин × рядків таблиці; десятки
    /// тисяч — уже ознака того, що прив'язку поставили на не ту таблицю.
    /// </remarks>
    private const int MaxResults = 50_000;

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Прогін добирається за <c>Status = Current</c>, а не за максимальним
    /// <c>Id</c>. Прогін, який упав, лишає по собі частину рядків, і «останній
    /// за часом» показав би суміш: половина чисел від нової версії методології,
    /// половина від старої, і жодної ознаки на екрані.
    ///
    /// ⚠ Актуальність питається ПІДЗАПИТОМ (<c>EXISTS</c>), а не з'єднанням:
    /// прогонів на період може бути кілька (кожен документ перераховується
    /// окремо), тож «актуальний прогін періоду» — не одне число, а з'єднання з
    /// проєкцією в тип, на полях якого потім сортують, EF перекласти не може
    /// взагалі.
    ///
    /// ⛔ Перевага ДОКУМЕНТНОГО прогону над ПРОЄКТНИМ (третя хвиля UX-PASS R4,
    /// «CalculationRun ховає результати сусідніх документів»): відколи
    /// <c>CalculationRun.DocumentId</c> розрізняє область прогону,
    /// <c>SwitchCurrentRunAsync</c> документного прогону НЕ знімає
    /// актуальність із прогону всього проєкту (той рахує й ІНШІ документи,
    /// чию актуальність гасити не можна) — тож обидва можуть бути
    /// <c>Current</c> ОДНОЧАСНО для того самого документа. Без переваги нижче
    /// результати документа читалися б із ДВОХ прогонів разом — те саме
    /// подвоєння, від якого захищає <c>UX_CalculationRun_Current</c>. Коли для
    /// документа є ВЛАСНИЙ актуальний прогін, читаємо ЛИШЕ з нього; інакше —
    /// з актуального прогону всього проєкту, як і раніше (документ, що ще
    /// ніколи не мав власного прогону, — типовий і сьогоднішній випадок).
    /// </remarks>
    public async Task<IReadOnlyList<CalculationResultRow>> ReadCurrentAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        var hasDedicatedCurrent = await db.CalculationRuns
            .AsNoTracking()
            .AnyAsync(
                r => r.DocumentId == documentId
                     && r.PeriodKey == periodKey
                     && r.Status == CalculationRun.CurrentStatus,
                ct)
            .ConfigureAwait(false);

        var rows = await db.CalculationResults
            .AsNoTracking()
            .Where(r => r.DocumentId == documentId
                        && r.PeriodKey == periodKey
                        && db.CalculationRuns.Any(
                            run => run.Id == r.CalculationRunId
                                   && run.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus
                                   && (run.DocumentId == documentId
                                       || (!hasDedicatedCurrent && run.DocumentId == null))))
            .OrderBy(r => r.SourceRowKey)
            .ThenBy(r => r.OutputCode)
            .Take(MaxResults)
            .Select(r => new
            {
                r.MethodologyVersionId,
                r.SourceRowKey,
                r.OutputCode,
                r.Value,
                r.UnitId,
                r.SubstanceEntryId,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(r => new CalculationResultRow(
            r.MethodologyVersionId, r.SourceRowKey, r.OutputCode, r.Value, r.UnitId, r.SubstanceEntryId));
    }

    /// <summary>
    /// Резервує безперервний діапазон ідентифікаторів кроків трейсу й
    /// повертає перший.
    /// </summary>
    /// <remarks>
    /// ⛔ Тут стояло <c>MAX(Id)+1</c> без блокування, а вставка відбувається аж
    /// на <c>SaveChanges</c> у <c>CalculationOutputWriter</c>.
    /// <c>CalculationOrchestrator</c> виконує методології пакета паралельно,
    /// кожну — зі своїм <c>EcrDbContext</c>, і всі пишуть трейс того самого
    /// прогону в той самий <c>PeriodKey</c>: гілки читали однаковий MAX, і всі,
    /// крім першої, падали на <c>PK_CalculationStep</c> — разом із
    /// результатами своєї методології (<c>CalculationStepIdRaceTests</c>).
    /// <para>
    /// Власної послідовності в кроків немає, а нова — це міграція. Тому Id
    /// береться зі спільної <c>calc.CalculationResultSeq</c>: ключ кроку —
    /// <c>(PeriodKey, Id)</c> у своїй таблиці, і перетин значень із
    /// <c>calc.CalculationResult</c> нічого не ламає — потрібна лише
    /// унікальність серед кроків.
    /// </para>
    /// <para>
    /// ⚠ Кроки, записані до цієї зміни, мають Id від 1 (старий MAX+1), і
    /// послідовність про них не знає. MAX читається ДО резервування: усе, що
    /// видала послідовність раніше, менше за наш діапазон, тож
    /// «перше &gt; MAX» означає, що старих Id попереду немає. Інакше значення
    /// до MAX спалюються одним резервуванням — один раз на базу, далі
    /// послідовність завжди попереду.
    /// </para>
    /// </remarks>
    private async Task<long> ReserveStepIdRangeAsync(int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var legacyMax = await db.CalculationSteps
            .AsNoTracking()
            .Select(s => (long?)s.Id)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? 0;

        // Запас на провайдера без послідовностей — як у ReserveResultIdRangeAsync.
        if (!db.Database.IsSqlServer())
        {
            return legacyMax + 1;
        }

        var first = await SequenceRangeAsync(count, ct).ConfigureAwait(false);
        if (first > legacyMax)
        {
            return first;
        }

        await SequenceRangeAsync(legacyMax - first + 1, ct).ConfigureAwait(false);
        return await SequenceRangeAsync(count, ct).ConfigureAwait(false);
    }
}
