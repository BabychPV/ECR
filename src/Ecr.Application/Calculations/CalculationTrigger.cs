// src/Ecr.Application/Calculations/CalculationTrigger.cs
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// Ставить автоматичний перерахунок документа за період (HSE301 §4.5, V-5 → <c>D-174</c>).
/// </summary>
/// <remarks>
/// ⛔ Без прапорця: нові дані без перерахунку однаково блокують подання
/// (<c>SubmitSheetHandler</c>), тобто прапорець лише відкладав би ту саму дію.
/// Кнопка «Перерахувати» лишається для «застаріло» з інших причин.
/// <para>
/// ⚠ Дедуплікація — НЕ тут. Задача ставиться маркером <see cref="IRecalculationJob"/> зі
/// сталою ціллю на пару «документ × період» (<see cref="AutoTargetOf"/>, <see cref="DependentsTargetOf"/>),
/// і черга сама лишає одну живу задачу на ціль. Друга, власна дедуплікація тут розійшлася б
/// із чергою на першій же зміні її ключа (узгоджено з «Аналізом», власницею черги).
/// ✎ B83: ціль автоперерахунку більше НЕ спільна з кнопкою (<see cref="RecalculateDocumentHandler.TargetOf"/>) —
/// інакше злиття губило б позначку поданих; два прогони серіалізує лок документа.
/// </para>
/// <para>
/// ⚠ Постановка — ОДНИМ викликом у <see cref="EnqueueSubmittedAsync"/>, і це
/// <c>EnqueueCoalescedAsync</c> (без витіснення), а не <c>EnqueueExclusiveAsync</c>:
/// автоперерахунок іде сплесками (три записи в один документ — три виклики), і
/// витіснення переривало б виконуваний перерахунок «гарячого» документа знову й
/// знову. Злиття ставить нову задачу ПІСЛЯ виконуваної (черга в базі) або
/// повертає наявну (Quartz). Лок документа (<c>ecr:recalc:doc:{id}</c>) бере сама
/// задача <see cref="IRecalculationJob"/> — тригер його не чіпає.
/// </para>
/// <para>
/// ⛔ B97 (F13 batch-5): сховища складу документа й копій аркушів — ОБОВ'ЯЗКОВІ. Доти вони були необов'язкові, і
/// без реєстрації склад документа «невідомий»: повністю затверджений документ із копіями аркушів ставився в чергу й
/// відмовлявся вже в задачі. Обов'язковий параметр ламає збірку чи резолюцію контейнера, а не мовчить.
/// </para>
/// </remarks>
public sealed class CalculationTrigger(
    IBackgroundJobScheduler jobs,
    IPeriodStore periods,
    IWorkflowStore workflow,
    IDocumentStore documents,
    IDocumentSheetCopyStore sheetCopies) : ICalculationTrigger
{
    /// <inheritdoc />
    public bool EnlistsInCallerTransaction => jobs.EnlistsInCallerTransaction;

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ B83 (слово людини 07.11: «тільки погоджені не мають перераховуватися»): автоперерахунок після збору PI і
    /// синхронізації подій — те саме правило, що в залежних (B63): пропускається лише ЗАТВЕРДЖЕНИЙ аркуш, чернетка
    /// поруч із поданим перераховується, поданий — із журналом і позначкою «перераховано після подання»
    /// (<c>RecalculateSubmitted</c>). Доти один поданий чи затверджений аркуш знімав із перерахунку ВЕСЬ документ.
    /// <para>
    /// ⚠ Нічний розклад сюди НЕ ходить (дочірні задачі <c>RecalculationJob.FanOutAsync</c>, ціль <c>TargetOf</c>,
    /// без позначки) і поданих не чіпає: інакше кожна ніч позначала б усі подані аркуші, і позначка нічого б не значила.
    /// </para>
    /// <para>
    /// ⚠ Ціль — своя (<see cref="AutoTargetOf"/>), а не <c>TargetOf</c>: черга зливає постановку з тією, що чекає, і
    /// позначка поданих загубилась би в нічній дочірній задачі чи правці шапки з тією самою ціллю (урок B63). Два
    /// прогони одного документа однаково не йдуть разом — лок документа бере сама задача.
    /// </para>
    /// </remarks>
    public async Task<string?> RequestAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        if (!await WritableAsync(documentId, periodKey, ct).ConfigureAwait(false))
        {
            return null;
        }

        return await EnqueueSubmittedAsync(AutoTargetOf(documentId, periodKey), documentId, periodKey, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Без шторму (D-577): відкладення <see cref="EditDebounce"/> від ПЕРШОЇ правки (злиття лишає раніший момент
    /// доступності — вікно не подовжується), ціль <see cref="AutoTargetOf"/> спільна зі збором PI і власним перерахунком
    /// документа-джерела (B179 L2b): масова вставка чи імпорт — одна задача на документ і період; правки під час прогону —
    /// ще ОДНА задача позаду нього. Задача системна (без автора) — під стелею важких перерахунків як «стеля − 1» і в черзі
    /// людей не обганяє ручне «Перерахувати» (D-564c). Закритий, не відкритий, архівований, затверджений — не ставиться
    /// (<see cref="WritableAsync"/>), подані перераховуються з позначкою (B83). Quartz (live) — те саме вікно в пам'яті (<c>QuartzJobScheduler.EnqueueCoalescedDelayedAsync</c>) і злиття на ціль.
    /// </remarks>
    public Task<string?> RequestAfterEditAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
        => RequestAfterEditAsync(documentId, periodKey, approverSheetDefIds: null, ct);

    /// <inheritdoc />
    /// <remarks>
    /// ✎ Огляд H2 (b): payload несе <c>SkipIfFresh</c> — задача нічого не рахує, якщо актуальний прогін уже новіший за останню
    /// зміну входів (людина встигла натиснути «Перерахувати», або злитий прогін це покрив). ✎ M5 (рішення людини, D-442):
    /// аркуші, які правив погоджувач поточного кроку (<paramref name="approverSheetDefIds"/>), — без позначки «перераховано після
    /// подання» (<c>ApproverSheets</c>).
    /// </remarks>
    public async Task<string?> RequestAfterEditAsync(
        long documentId, PeriodKey periodKey, IReadOnlyCollection<int>? approverSheetDefIds, CancellationToken ct)
    {
        if (!await WritableAsync(documentId, periodKey, ct).ConfigureAwait(false))
        {
            return null;
        }

        int[]? approver = approverSheetDefIds is { Count: > 0 } ? [.. approverSheetDefIds.Distinct().Order()] : null;
        var payload = new
        {
            DocumentId = documentId,
            PeriodKey = periodKey.Value,
            TriggeredByUserId = (int?)null,
            SheetDefId = (int?)null,
            RecalculateSubmitted = true,
            SkipIfFresh = true,
            ApproverSheets = approver,
        };

        return await jobs
            .EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                AutoTargetOf(documentId, periodKey), payload, EditDebounce, ct, createdByUserId: null)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// ✎ D-577 (Q2): вікно злиття правок людини перед автоперерахунком методологій (рекомендація плану — ~20 с тиші; черга
    /// рахує від першої правки сплеску, тож старт — щонайбільше через це вікно після першої правки).
    /// </summary>
    public static readonly TimeSpan EditDebounce = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    /// <remarks>
    /// ✎ PERF-06b (<c>D-572</c>): місяці — у payload (<c>SourceMonths</c>) лише коли черга зливає постановки об'єднанням
    /// (<c>DbJobQueue.MergePressFieldsSql</c>: обидві з місяцями — об'єднання, хоч одна без — без місяців, тобто увесь рік). Quartz
    /// лишає payload першої постановки — місяці другої правки загубилися б, тож там без них (увесь рік, як доти).
    /// </remarks>
    public async Task<string?> RequestAsync(
        long documentId, PeriodKey periodKey, IReadOnlyCollection<int> sourceMonths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceMonths);
        if (!await WritableAsync(documentId, periodKey, ct).ConfigureAwait(false))
        {
            return null;
        }

        // ✎ PERF-06b (D-572, live Quartz): і Quartz зливає місяці об'єднанням (QuartzSourceMonths) — не лише черга в базі.
        int[]? months = jobs.MergesSourceMonths && sourceMonths.Count > 0 ? [.. sourceMonths.Distinct().Order()] : null;
        return await EnqueueSubmittedAsync(AutoTargetOf(documentId, periodKey), documentId, periodKey, ct, sourceMonths: months)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ B63 (слово людини 07.11: «тільки погоджені не мають перераховуватися»): залежні від довідника чи
    /// документа-джерела (RT-25, HSE400 3b, FLERT F6) перераховуються ПО АРКУШУ — пропускається лише затверджений;
    /// чернетка, повернутий і поданий перераховуються, поданий — із записом журналу й позначкою
    /// «перераховано після подання» (той самий механізм, що в ручного перерахунку: <c>RecalculationJob</c>
    /// з <c>RecalculateSubmitted</c>). Доти один поданий чи затверджений аркуш знімав із перерахунку ВЕСЬ документ.
    /// <para>
    /// ⚠ «Рахувати нічого» — затверджено КОЖЕН аркуш складу (оригінал і кожна копія, RS), те саме правило, що в
    /// <c>RecalculateDocumentHandler</c>; інакше задача відмовила б 4221 і лишила проваленого перерахунку в журналі.
    /// Склад невідомий — «нічого», лише коли всі наявні стани аркушів затверджені.
    /// </para>
    /// <para>
    /// ⚠ Ціль — своя (<see cref="DependentsTargetOf"/>), а не <c>TargetOf</c>: черга зливає постановку з тією, що
    /// чекає, і payload залежних без позначки поданих загубився б в автоперерахунку збору. Два прогони одного
    /// документа однаково не йдуть разом — лок документа бере сама задача.
    /// </para>
    /// </remarks>
    public Task<string?> RequestDependentsAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
        => RequestDependentsAsync(documentId, periodKey, sourceHop: 0, ct);

    /// <inheritdoc />
    /// <remarks>
    /// B179 L2: ланка їде в payload (<c>SourceHop</c>); перерахунок споживача передає її хуку завершення. Ціль та сама
    /// (<see cref="DependentsTargetOf"/>).
    /// <para>
    /// ⚠ Злиття в задачу, що ЧЕКАЄ, лишає payload (і ланку) ПЕРШОЇ постановки: черга в базі не переписує payload задачі без
    /// масиву злиття (<c>DbJobQueue.EnqueueSql</c>, лише <c>AvailableAt</c>). Так само покриття задачею «з затвердженими»
    /// лишає ЇЇ ланку. Ланка важить лише для стелі ланцюжка (<c>RegistryImpactRecalculationRequest.MaxHops</c>).
    /// </para>
    /// </remarks>
    public Task<string?> RequestDependentsAsync(long documentId, PeriodKey periodKey, int sourceHop, CancellationToken ct)
        => RequestDependentsAsync(documentId, periodKey, sourceHop, approvedOverride: null, ct);

    /// <inheritdoc />
    /// <remarks>
    /// ✎ B80 N5 (D-554): з перекриттям адміністратора — «рахувати нічого, бо всі аркуші затверджені» НЕ відмовляє
    /// (затверджені якраз і перераховуються); стан періоду й архів перевіряються як завжди. Ціль черги — своя
    /// (<see cref="DependentsApprovedTargetOf"/>): задача з перекриттям не зливається з задачею без нього і навпаки.
    /// </remarks>
    public Task<string?> RequestDependentsAsync(
        long documentId, PeriodKey periodKey, int sourceHop, ApprovedRecalculationOverride? approvedOverride, CancellationToken ct)
        => RequestDependentsCoreAsync(documentId, periodKey, sourceHop, approvedOverride, document: null, ct);

    /// <summary>
    /// Тіло <see cref="RequestDependentsAsync(long, PeriodKey, int, ApprovedRecalculationOverride?, CancellationToken)"/>; ✎ PERF-06b
    /// (<c>D-572</c>): <paramref name="document"/> — пакетні читання документа на кілька періодів (каскад на черзі без об'єднання).
    /// </summary>
    private async Task<string?> RequestDependentsCoreAsync(
        long documentId, PeriodKey periodKey, int sourceHop, ApprovedRecalculationOverride? approvedOverride,
        DocumentWritability? document, CancellationToken ct)
    {
        if (!await WritableAsync(documentId, periodKey, ct, ignoreAllApproved: approvedOverride is not null, document: document).ConfigureAwait(false))
        {
            return null;
        }

        // ✎ Перф-аудит 20.11: «залежні з затвердженими» (`-dependents-approved`) — НАДМНОЖИНА «залежних» (`-dependents`) для
        // того самого документа й періоду: ті самі подані й чернетки (`RecalculateSubmitted`), плюс затверджені, які звичайна
        // задача пропускає. Дві цілі не зливаються (payload одного не годиться іншому), а лок документа лише СЕРІАЛІЗУЄ
        // прогони — обидва виконувались би повністю (на Land ≈ 12 хв кожен). Тому: звичайну, що чекає, скасовує постановка з
        // перекриттям; звичайна, що приходить, коли така вже чекає, не ставиться — вона виконає ту саму роботу з надлишком.
        // ⚠ Лише для задачі, що ЧЕКАЄ: виконувана могла прочитати входи до цієї зміни, тож її не скасовуємо й не покладаємось.
        // ⛔ Пошук без локу: у НЕЗАКОМІЧЕНІЙ транзакції викликача планувальник у базі покриття не дає (`FindQueuedAsync`
        // → null), бо знайдена задача могла б стартувати й прочитати входи до коміту зміни. Нині викликач (задача
        // RegistryImpactRecalculationJob) транзакції не відкриває — це запобіжник на майбутнє, а не робочий шлях.
        // ✎ Задача з перекриттям, що вже СТАРТУВАЛА (лок документа взято), сама скасовує звичайну, яка встигла стати в
        // чергу, поки вона чекала лок (`RecalculationJob`): входи вона читає вже після того.
        var approvedTarget = DependentsApprovedTargetOf(documentId, periodKey);
        if (approvedOverride is null
            && (document?.Covered is { } covered
                ? covered.GetValueOrDefault(approvedTarget)
                : await jobs.FindQueuedAsync<IRecalculationJob>(approvedTarget, ct).ConfigureAwait(false))
                is { Length: > 0 } coveringJobId)
        {
            return coveringJobId;
        }

        // ✎ PSS-07 (D-561, рішення людини 20.11): залежні — КАСКАД від зміни іншого документа чи довідника, не пряма
        // правка: ставляться з відкладенням CascadeDebounce, і завершення кількох джерел за це вікно дають ОДИН перерахунок
        // споживача за період (злиття лишає перший момент доступності — вікно не подовжується). «З затвердженими» — явна дія
        // адміністратора: без відкладення.
        var jobId = await EnqueueSubmittedAsync(
                approvedOverride is null ? DependentsTargetOf(documentId, periodKey) : DependentsApprovedTargetOf(documentId, periodKey),
                documentId, periodKey, ct, sourceHop, approvedOverride,
                approvedOverride is null ? CascadeDebounce : TimeSpan.Zero)
            .ConfigureAwait(false);

        if (approvedOverride is not null)
        {
            // Після постановки, а не до: у жодний момент цілі без задачі, що виконає роботу, немає. Скасована несе JobId
            // покривної (повідомлення в переліку задач). ⚠ Покривна, що провалиться чи буде скасована, повертає звичайну
            // (`ApprovedDependentsFallback`): інакше покрита робота губилася б разом із нею.
            await jobs.CancelQueuedAsync<IRecalculationJob>(DependentsTargetOf(documentId, periodKey), jobId, ct)
                .ConfigureAwait(false);
        }

        return jobId;
    }

    /// <inheritdoc />
    /// <remarks>✎ PERF-06 (<c>D-566</c>): лише коли черга об'єднує періоди каскаду при злитті (черга в базі).</remarks>
    public bool CoalescesCascadePeriods => jobs.MergesCascadePeriods;

    /// <inheritdoc />
    public bool CarriesSourceMonths => jobs.MergesSourceMonths;

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// ✎ PERF-06 (<c>D-566</c>): ціль <see cref="SourceCascadeTargetOf"/> (<c>doc{id}-y{рік}-dependents</c>) — системні залежні під
    /// стелею важких перерахунків і межею розкладу (<c>DbJobQueue.CascadeTargetLike</c>), без автора; відкладення — те саме вікно
    /// <see cref="CascadeDebounce"/>, що й поперіодних залежних (PSS-07, межа людини 15 с), від ПЕРШОЇ постановки (не
    /// подовжується). Злиття в задачу, що чекає, — об'єднання <c>cascadePeriods</c>; у ланцюжок, що вже йде (між кроками чи
    /// позаду кроку), — <c>cascadeAppend</c>: наступний крок дописує ці періоди й тоді, коли вони вже зроблені.
    /// </para>
    /// <para>
    /// Періоди, у які писати не можна (закритий, <c>Scheduled</c>, архів, затверджено все), і ті, що вже чекають у задачі «з
    /// затвердженими» (надмножина роботи, B80 N5), відкидаються ТУТ — як у поперіодній постановці. ✎ Огляд F2: другі — не
    /// зовсім: вони йдуть затравками (<c>cascadeSeeds</c>) пізніших читачів, яких задача названого періоду не будить. Черга
    /// без об'єднання (<see cref="CoalescesCascadePeriods"/> = <c>false</c>) — поперіодно, як до D-566.
    /// </para>
    /// <para>
    /// ✎ Огляд F4: ланка джерел <paramref name="sourceHop"/> при злитті в задачу, що чекає чи йде, — МЕНША з двох
    /// (<c>DbJobQueue.MergePressFieldsSql</c>), а не перша.
    /// </para>
    /// </remarks>
    public async Task<SourceCascadeEnqueued> RequestSourceCascadeAsync(
        long documentId, int year, IReadOnlyCollection<int> periodKeys, int sourceHop, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);
        if (!CoalescesCascadePeriods)
        {
            // ✎ PERF-06b (D-572): і поперіодно (Quartz, live InProcess) — читання документа пакетом на всі його періоди.
            string? last = null;
            var each = new List<int>();
            var perPeriod = periodKeys.Distinct().Order().ToList();
            var prefetched = await PrefetchAsync(documentId, perPeriod, ct).ConfigureAwait(false);
            foreach (var period in perPeriod)
            {
                if (await RequestDependentsCoreAsync(documentId, new PeriodKey(period), sourceHop, null, prefetched, ct).ConfigureAwait(false) is { } id)
                {
                    last = id;
                    each.Add(period);
                }
            }

            return new SourceCascadeEnqueued(last, each);
        }

        var periods = new List<int>();
        var seeds = new List<int>();

        // ✎ PERF-06b (D-572, вимір 200 споживачів): архівність проєкту й склад аркушів документа — один раз на документ, стани
        // періодів і аркушів — одним зверненням кожне на всі періоди (ребро «рік»: 12 періодів на споживача), а не на кожен період.
        var candidates = periodKeys.Where(p => p / 100 == year).Distinct().Order().ToList();
        var document = await PrefetchAsync(documentId, candidates, ct).ConfigureAwait(false);

        foreach (var period in candidates)
        {
            var key = new PeriodKey(period);
            if (!await WritableAsync(documentId, key, ct, document: document).ConfigureAwait(false))
            {
                continue;
            }

            // ✎ Огляд F2: період уже чекає в задачі «з затвердженими» — його рахує вона, але вона (названий період) будить
            // лише РАНІШИХ читачів; пізніші місяці, що читають його обчислені числа ([Period:-1]), будить ця задача —
            // період іде затравкою (cascadeSeeds), а не періодом до розрахунку.
            var approvedTarget = DependentsApprovedTargetOf(documentId, key);
            if (document.Covered is { } covered
                    ? covered.ContainsKey(approvedTarget)
                    : await jobs.FindQueuedAsync<IRecalculationJob>(approvedTarget, ct).ConfigureAwait(false) is { Length: > 0 })
            {
                seeds.Add(period);
                continue;
            }

            periods.Add(period);
        }

        if (periods.Count == 0 && seeds.Count == 0)
        {
            return new SourceCascadeEnqueued(null, []);
        }

        var payload = new
        {
            DocumentId = documentId,
            PeriodKey = (int?)null,
            Year = year,
            TriggeredByUserId = (int?)null,
            SheetDefId = (int?)null,
            RecalculateSubmitted = true,
            SourceHop = sourceHop,
            CascadePeriods = periods.ToArray(),
            CascadeSeeds = seeds.Count > 0 ? seeds.ToArray() : null,
        };

        var jobId = await jobs
            .EnqueueCoalescedDelayedAsync<IRecalculationJob>(
                SourceCascadeTargetOf(documentId, year), payload, CascadeDebounce, ct, createdByUserId: null)
            .ConfigureAwait(false);
        return new SourceCascadeEnqueued(jobId, periods);
    }

    /// <summary>
    /// ✎ PERF-06 (<c>D-566</c>): ціль каскаду від правки документа-джерела — одна на документ-споживач і рік; закінчення
    /// <c>-dependents</c> — системні залежні під стелею (<c>DbJobQueue.CascadeTargetLike</c>).
    /// </summary>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="year">Рік.</param>
    /// <returns><c>doc{id}-y{рік}-dependents</c>.</returns>
    public static string SourceCascadeTargetOf(long documentId, int year)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{RecalculateDocumentHandler.YearScopedTargetOf(documentId, year, null)}-dependents");

    /// <summary>
    /// ✎ PSS-07 (<c>D-561</c>): відкладення каскадного перерахунку (залежні від зміни документа-джерела чи довідника) — вікно, за яке постановки
    /// на ту саму ціль зливаються в одну.
    /// </summary>
    /// <remarks>
    /// ⛔ Рішення людини 20.11: не більше <see cref="MaxCascadeDebounce"/> (не 30–60 с аудиту); пряма правка (задача формул
    /// після PATCH), автоперерахунок після збору PI (<c>D-174</c>, <c>-auto</c>) і ручний перерахунок не відкладаються —
    /// <c>ФВ-12.2</c> (&lt; 10 с авто, &lt; 5 с ручний) для них діє як доти. 10 с, а не 15: claim опитує чергу раз на
    /// секунду, тож старт каскаду лишається в межах 15 с з запасом.
    /// </remarks>
    public static readonly TimeSpan CascadeDebounce = TimeSpan.FromSeconds(10);

    /// <summary>Межа відкладення каскаду з рішення людини 20.11 (<c>D-561</c>); сторож — тест <c>CascadeDebounceDbTests</c>.</summary>
    public static readonly TimeSpan MaxCascadeDebounce = TimeSpan.FromSeconds(15);

    /// <summary>Закінчення цілі <see cref="DependentsApprovedTargetOf"/> — дешевий відбір задач «з затвердженими» за ключем цілі.</summary>
    public const string DependentsApprovedSuffix = "-dependents-approved";

    /// <summary>Ціль перерахунку залежних З перекриттям адміністратора (затверджені теж): окрема від <see cref="DependentsTargetOf"/>.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <returns><c>doc{id}-p{період}-dependents-approved</c>.</returns>
    public static string DependentsApprovedTargetOf(long documentId, PeriodKey periodKey)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{RecalculateDocumentHandler.TargetOf(documentId, periodKey)}{DependentsApprovedSuffix}");

    /// <summary>Ціль перерахунку залежних: окрема від автоперерахунку збору й кнопки (див. <see cref="RequestDependentsAsync(long, Ecr.Domain.ValueObjects.PeriodKey, CancellationToken)"/>).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <returns><c>doc{id}-p{період}-dependents</c>.</returns>
    public static string DependentsTargetOf(long documentId, PeriodKey periodKey)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{RecalculateDocumentHandler.TargetOf(documentId, periodKey)}-dependents");

    /// <summary>
    /// B83: ціль автоперерахунку після збору PI і синхронізації подій — окрема від кнопки, правки шапки й нічних
    /// дочірніх задач (<c>TargetOf</c>), щоб злиття не губило позначку поданих (див. <see cref="RequestAsync(long, PeriodKey, CancellationToken)"/>).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <returns><c>doc{id}-p{період}-auto</c>.</returns>
    public static string AutoTargetOf(long documentId, PeriodKey periodKey)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{RecalculateDocumentHandler.TargetOf(documentId, periodKey)}-auto");

    /// <summary>
    /// Чи є що перераховувати без людини: період відомий, не <c>Scheduled</c> і не закритий, проєкт не архівований і не
    /// архівується, і хоч один аркуш складу (оригінал чи копія) ще не затверджений.
    /// </summary>
    /// <remarks>
    /// ⛔ Тут правило не гейт (гейт — у <c>RecalculationJob</c>), а відмова ставити задачу, яка однаково відмовила б:
    /// інакше кожен пізній збір за закритий період чи в архівованому проєкті лишав би в журналі задач провалений
    /// перерахунок.
    /// <para>
    /// ⛔ B97: архівований (чи той, що архівується) проєкт — та сама перевірка B69, що в кнопки
    /// (<c>RecalculateDocumentHandler</c>) і в задачі: виняток «місяць відкрито для документа», що пережив архівацію,
    /// піднімає закритий місяць до <c>Grace</c>, тож самого стану періоду не досить.
    /// </para>
    /// </remarks>
    private async Task<bool> WritableAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct, bool ignoreAllApproved = false, DocumentWritability? document = null)
    {
        var state = document?.States is { } states && states.TryGetValue(periodKey.Value, out var known)
            ? known
            : await periods.FindPeriodStateAsync(documentId, periodKey.Value, ct).ConfigureAwait(false);
        if (state is not { } periodState || periodState == PeriodState.Scheduled
            || RecalculationWritePolicy.Check(periodState, hasSubmittedSheets: false) != RecalculationWriteDenial.None)
        {
            return false;
        }

        var archived = document?.Archived
                       ?? await periods.IsDocumentProjectArchivedOrArchivingAsync(documentId, ct).ConfigureAwait(false);
        if (document is not null)
        {
            document.Archived = archived;
        }

        if (archived)
        {
            return false;
        }

        if (ignoreAllApproved)
        {
            return true;
        }

        var sheets = document?.Sheets is { } byPeriod && byPeriod.TryGetValue(periodKey.Value, out var prefetched)
            ? prefetched
            : await workflow.GetSheetsAsync(documentId, periodKey, ct).ConfigureAwait(false);
        return !await AllApprovedAsync(documentId, sheets, ct, document).ConfigureAwait(false);
    }

    /// <summary>
    /// «Рахувати нічого»: затверджено КОЖЕН аркуш складу (оригінал і кожна копія, RS) — те саме правило, що в
    /// <c>RecalculateDocumentHandler</c>. Склад невідомий — лише коли всі наявні стани аркушів затверджені.
    /// </summary>
    private async Task<bool> AllApprovedAsync(
        long documentId, IReadOnlyList<Domain.Entities.Workflow.ApprovalState> sheets, CancellationToken ct,
        DocumentWritability? document = null)
    {
        bool Approved(int sheetId, long? copyId) => sheets.Any(s =>
            s.SheetDefId == sheetId && s.SheetCopyId == copyId && s.Status == DocumentStatus.Approved);

        var included = document?.Included ?? await documents.GetIncludedSheetIdsAsync(documentId, ct).ConfigureAwait(false);
        if (document is not null)
        {
            document.Included = included;
        }

        if (included.Count == 0)
        {
            return sheets.Count > 0 && sheets.All(s => s.Status == DocumentStatus.Approved);
        }

        var copiesBySheet = document?.CopiesBySheet
                            ?? (await sheetCopies.ListGroupsAsync(documentId, ct).ConfigureAwait(false))
                                .ToDictionary(g => g.SheetDefId, g => g.Copies);
        if (document is not null)
        {
            document.CopiesBySheet = copiesBySheet;
        }

        return included.All(sheetId => Approved(sheetId, null)
            && (copiesBySheet.GetValueOrDefault(sheetId) ?? []).All(c => Approved(sheetId, c.SheetCopyId)));
    }

    /// <summary>
    /// ✎ PERF-06b (<c>D-572</c>): частини перевірки «чи є що перераховувати», що залежать лише від документа (архівність проєкту,
    /// склад аркушів, копії), — один раз на документ у межах однієї постановки каскаду кількох періодів.
    /// </summary>
    private async Task<DocumentWritability> PrefetchAsync(long documentId, List<int> candidates, CancellationToken ct)
    {
        var document = new DocumentWritability();
        if (candidates.Count > 1)
        {
            document.States = await periods.FindPeriodStatesAsync(documentId, candidates, ct).ConfigureAwait(false);
            document.Sheets = await workflow.GetSheetsForPeriodsAsync(documentId, candidates, ct).ConfigureAwait(false);
            document.Covered = await jobs
                .FindQueuedManyAsync<IRecalculationJob>(
                    [.. candidates.Select(p => DependentsApprovedTargetOf(documentId, new PeriodKey(p)))], ct)
                .ConfigureAwait(false);
        }

        return document;
    }

    private sealed class DocumentWritability
    {
        /// <summary>Задачі «з затвердженими», що чекають, за ціллю (огляд F2 — затравки).</summary>
        public IReadOnlyDictionary<string, string>? Covered { get; set; }

        public IReadOnlyDictionary<int, PeriodState?>? States { get; set; }

        public IReadOnlyDictionary<int, IReadOnlyList<Domain.Entities.Workflow.ApprovalState>>? Sheets { get; set; }

        public bool? Archived { get; set; }

        public IReadOnlyList<int>? Included { get; set; }

        public Dictionary<int, IReadOnlyList<SheetCopyOrdinal>>? CopiesBySheet { get; set; }
    }

    /// <summary>Єдина точка постановки системного перерахунку, що пише й подані аркуші.</summary>
    /// <remarks>
    /// ⚠ Payload — форма <c>RecalculationRequest</c>; <c>TriggeredByUserId</c> і автор задачі — <c>null</c>: це
    /// системна задача, людини, що натиснула кнопку, немає (Q-156). <c>RecalculateSubmitted</c> — подані
    /// перераховуються з журналом і позначкою, затверджені пропускає сама задача.
    /// </remarks>
    private Task<string> EnqueueSubmittedAsync(
        string target, long documentId, PeriodKey periodKey, CancellationToken ct, int sourceHop = 0,
        ApprovedRecalculationOverride? approvedOverride = null, TimeSpan delay = default, int[]? sourceMonths = null)
    {
        var payload =
            // ✎ PERF-06b (D-572): власний перерахунок річного/квартального джерела з місяцями правки.
            sourceMonths is not null
                ? (object)new
                {
                    DocumentId = documentId,
                    PeriodKey = periodKey.Value,
                    TriggeredByUserId = (int?)null,
                    SheetDefId = (int?)null,
                    RecalculateSubmitted = true,
                    SourceMonths = sourceMonths,
                }
                :
            // B80 N5: перекриття адміністратора — затверджені теж (IncludeApproved) з автором і причиною для журналу.
            approvedOverride is not null
                ? (object)new
                {
                    DocumentId = documentId,
                    PeriodKey = periodKey.Value,
                    TriggeredByUserId = (int?)null,
                    SheetDefId = (int?)null,
                    RecalculateSubmitted = true,
                    SourceHop = sourceHop,
                    IncludeApproved = true,
                    OverrideUserId = (int?)approvedOverride.UserId,
                    OverrideReason = approvedOverride.Reason,
                }
                :
            // B179 L2: ланка — лише коли вона є (форма payload без ланки — та сама, що доти).
            sourceHop > 0
                ? (object)new
                {
                    DocumentId = documentId,
                    PeriodKey = periodKey.Value,
                    TriggeredByUserId = (int?)null,
                    SheetDefId = (int?)null,
                    RecalculateSubmitted = true,
                    SourceHop = sourceHop,
                }
                : new
                {
                    DocumentId = documentId,
                    PeriodKey = periodKey.Value,
                    TriggeredByUserId = (int?)null,
                    SheetDefId = (int?)null,
                    RecalculateSubmitted = true,
                };

        // Без відкладення — та сама постановка, що доти (PSS-07 відкладає лише каскад).
        return delay > TimeSpan.Zero
            ? jobs.EnqueueCoalescedDelayedAsync<IRecalculationJob>(target, payload, delay, ct, createdByUserId: null)
            : jobs.EnqueueCoalescedAsync<IRecalculationJob>(target, payload, ct, createdByUserId: null);
    }
}
