using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Перерахунок: інкрементний за dirty-set або повний за адміністративною
/// командою.
/// </summary>
/// <remarks>
/// **Бюджет повного річного перерахунку — ≤ 10 хвилин** (ПРД-13). Базова лінія
/// чинної системи — 20 хвилин, і формулювання «не гірше» тут не застосовується.
/// Паралельність за пакетами графа і пакетне читання входів живуть в
/// <c>CalculationOrchestrator</c>; тут — розбір завдання, життєвий цикл
/// прогону і завершення.
/// <para>
/// ⚠ Клас реалізує <see cref="IRecalculationJob"/>, а не лише
/// <c>IBackgroundJob</c>. Маркер існує саме щоб use-case міг назвати задачу,
/// не знаючи її реалізації; без нього <c>EnqueueAsync&lt;IRecalculationJob&gt;</c>
/// не мав би що запустити — черга приймала б завдання, і не робилося б нічого.
/// </para>
/// <para>
/// ⛔ Прогін складається з ДВОХ конвеєрів, і порядок між ними гарантує КОД, а
/// не порядок викликів клієнта: спершу формули шаблону (<c>doc.CellValue</c>),
/// потім методології (<c>calc.CalculationResult</c>). Причина — у тому, звідки
/// методологія бере входи: <c>CalculationInputBuilder</c> читає
/// <c>ICellStore.ReadSliceAsync</c>, тобто рівно ту таблицю, у яку пишуть
/// формули шаблону. Порахувати методології першими означало б узяти входи
/// ДО того, як вони стали правильними, — і видати новий прогін із новою
/// контрольною сумою від застарілих чисел. Неправильне число без жодної
/// ознаки неправильності (директива №10 `W10.1`).
/// </para>
/// </remarks>
public sealed class RecalculationJob(
    EcrDbContext db,
    ICalculationRunner orchestrator,
    RunCalculationHandler runs,
    RecalculationService formulas,
    Domain.Abstractions.IClock clock) : IRecalculationJob
{
    /// <summary>Стеля прив'язок на прогін: методологій у системі — десятки.</summary>
    private const int MaxBindings = 5_000;

    /// <summary>Скільки шкали прогресу віддано формулам шаблону.</summary>
    /// <remarks>
    /// ⚠ Оркестратор методологій рахує власні відсотки від нуля
    /// (<c>CalculationOrchestrator</c>). Без масштабування шкала стрибала б
    /// назад — 40 %, потім знову 5 %, — і «скільки лишилося» перестало б
    /// щось означати саме тоді, коли прогін довгий і на нього дивляться.
    /// </remarks>
    private const int FormulaPhaseShare = 40;

    /// <summary>Налаштування розбору завдання; спільні на всі виклики.</summary>
    /// <remarks>
    /// Один екземпляр на клас, а не на виклик: <c>JsonSerializerOptions</c>
    /// кешує метадані типів усередині, і новий об'єкт щоразу означає повторний
    /// розбір рефлексією на кожне завдання черги.
    /// </remarks>
    private static readonly JsonSerializerOptions PayloadOptions =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = Parse(payload);

        // ⛔ `ProjectId` визначається з ДОКУМЕНТА, коли payload його не несе, а
        // не приймається як 0. `RecalculateDocumentHandler` кладе в чергу
        // `new { DocumentId, PeriodKey }` — без `ProjectId` узагалі; при
        // розборі в non-nullable `int` це мовчки стає `0`. `CalculationRun`
        // із `ProjectId = 0` не проходить `FK_CalculationRun_Project`, і
        // `SaveChangesAsync` нижче кидав `SqlException 547` — виміряно живим
        // прогоном (директива №09 §1.3).
        var projectId = request.ProjectId > 0
            ? request.ProjectId
            : await db.Documents
                .AsNoTracking()
                .Where(d => d.Id == request.DocumentId)
                .Select(d => d.ProjectId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        var run = new Domain.Entities.Calculations.CalculationRun(
            projectId, request.PeriodKey, request.TriggeredByUserId, clock.UtcNow);

        try
        {
            // ⛔ Створення й ПЕРШЕ збереження — ВСЕРЕДИНІ `try`, а не до нього.
            // Раніше стояли до `try`: коли сам `INSERT` провалювався (рівно
            // так і сталося з `ProjectId = 0` вище), виняток летів МИМО catch
            // нижче — і задача лишалася `Running` назавжди, хоча catch
            // виглядав так, ніби мав це перехопити.
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // ⛔ Q-151/Q-162 (аудит фази 1). `RunCalculationHandler` УЖЕ ставив
            // у чергу payload без `DocumentId` (нуль після розбору JSON) —
            // документація поля прямо казала «нуль — усі документи проєкту»,
            // але сюди ніхто не дійшов: фільтр нижче на `DocumentId == 0`
            // завжди повертав ПОРОЖНІЙ перелік прив'язок, а прогін завершувався
            // `Succeeded` над нулем документів. Тепер `DocumentId <= 0` справді
            // означає «усі документи проєкту» — рішення людини: «так, потрібен
            // явний маршрут».
            var documentIds = request.DocumentId > 0
                ? (IReadOnlyList<long>)[request.DocumentId]
                : await ProjectDocumentIdsAsync(projectId, ct).ConfigureAwait(false);

            var totalProfile = new ModuleProfile();

            for (var i = 0; i < documentIds.Count; i++)
            {
                var documentId = documentIds[i];
                var prefix = documentIds.Count > 1
                    ? $"Документ {documentId} ({i + 1} із {documentIds.Count}): "
                    : string.Empty;

                // ⚠ Один документ — той самий діапазон 0…100, що й завжди
                // (i=0, Count=1 дає floor=0, ceiling=100): жодна наявна
                // поведінка не змінюється. Кілька документів ділять шкалу
                // порівну між собою.
                var floor = i * 100 / documentIds.Count;
                var ceiling = (i + 1) * 100 / documentIds.Count;
                var formulaCeiling = floor + ((ceiling - floor) * FormulaPhaseShare / 100);

                // ⛔ КРОК 1 — формули шаблону, і саме ВСЕРЕДИНІ `try`. Відмова
                // тут мусить позначити прогін `Failed` із причиною, а не
                // лишити його `Running` назавжди: рівно цей клас дефекту в
                // цьому файлі вже коштував розбору двічі (`D2-285`, `D2-286`).
                await progress
                    .ReportAsync(floor, $"{prefix}Перерахунок формул шаблону.", ct)
                    .ConfigureAwait(false);

                var docRequest = request with { DocumentId = documentId };
                var cells = await FormulasAsync(docRequest, ct).ConfigureAwait(false);

                await progress
                    .ReportAsync(
                        formulaCeiling, $"{prefix}Формули шаблону: перераховано комірок — {cells}.", ct)
                    .ConfigureAwait(false);

                var bindings = await BindingsAsync(docRequest, ct).ConfigureAwait(false);

                // ⛔ КРОК 2 — методології, і лише тепер: їхні входи щойно
                // стали актуальними.
                await progress
                    .ReportAsync(formulaCeiling, $"{prefix}Перерахунок методологій.", ct)
                    .ConfigureAwait(false);

                // ⛔ ОДИН прогін (`run.Id`) на всі документи — `CalculationRun`
                // прив'язаний до проєкту й періоду (`FK_CalculationRun_Project`),
                // не до документа. Профілі модулів зводяться в один сумарний
                // запис нижче — `ModuleProfile.Record` акумулює за кодом
                // модуля, тож повторний виклик на кожен документ саме те, для
                // чого метод і існує.
                var profile = await orchestrator
                    .RunAsync(
                        run.Id,
                        documentId,
                        new PeriodKey(request.PeriodKey ?? 0),
                        bindings,
                        new PhaseProgress(progress, formulaCeiling, ceiling, "Методології"),
                        ct)
                    .ConfigureAwait(false);

                totalProfile.Merge(profile);
            }

            // Завершення — прикладний сценарій: профіль і перемикання
            // актуальності однією транзакцією (ФВ-9.11).
            await runs.CompleteAsync(run.Id, totalProfile, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ⛔ Трекер очищається ПЕРЕД повторним записом. `EcrDbContext` тут
            // — ТОЙ САМИЙ скоуп-інстанс, яким `QuartzJobAdapter` пише
            // `itg.JobProgress` (`JobProgressStore`, той самий DI-скоуп): якщо
            // лишити в трекері сутність, чий `INSERT` щойно провалився,
            // НАСТУПНЕ `SaveChangesAsync` — навіть чуже, запис прогресу в
            // `itg.JobProgress` — повторно спробує вставити той самий
            // зіпсований рядок і провалиться теж. Тоді `QuartzJobAdapter`
            // не зможе позначити задачу `Failed`, і вона лишиться `Running`
            // назавжди — це і є справжня причина «задача висить 0 %»
            // (директива №09 §1.3), а не сам факт «catch не викликається».
            db.ChangeTracker.Clear();

            // Прогін позначається `Failed` лише якщо його `INSERT` УСПІШНО
            // відбувся (`run.Id` призначений базою): позначати нема чого,
            // якщо самого рядка в базі немає.
            if (run.Id > 0)
            {
                db.CalculationRuns.Attach(run);
                run.Complete("Failed", clock.UtcNow, profileJson: null, errorMessage: ex.Message);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Повний перерахунок формул шаблону для документа й періодів завдання.</summary>
    /// <returns>Скільки комірок перераховано.</returns>
    /// <remarks>
    /// ⛔ Повний, а не інкрементний. Інкрементний шлях
    /// (<c>PatchCellsHandler</c> → <c>IFormulaRecalculationJob</c>) бере
    /// формули з набору змінених комірок, тож формула, ДОДАНА в шаблон після
    /// введення даних, не потрапляє в нього ніколи — і залишалася б
    /// непорахованою нескінченно.
    ///
    /// ⚠ Періоди перебираються ЗА ЗРОСТАННЯМ. Формула шаблону має право
    /// читати попередній період (<c>[Period:-1]</c>), і зворотний порядок
    /// порахував би лютий зі старого січня, а потім січень — правильно, але
    /// вже нікому.
    /// </remarks>
    private async Task<int> FormulasAsync(RecalculationRequest request, CancellationToken ct)
    {
        // ⚠ Періоди беруться з ЕКЗЕМПЛЯРІВ таблиць, а не з `request.PeriodKey`:
        // той може бути `null` — «повний рік», — і `new PeriodKey(0)` тоді
        // виглядав би як звичайний період, у якому просто нічого немає
        // (`A7-28`, `PeriodKey.IsValid`).
        var scopes = await db.TableInstances
            .AsNoTracking()
            .Where(i => i.DocumentId == request.DocumentId
                        && (request.PeriodKey == null || i.PeriodKeyValue == request.PeriodKey))
            .Select(i => new ScopeRow(i.DocumentId, i.PeriodKeyValue))
            .Distinct()
            .Take(MaxBindings)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var written = 0;

        foreach (var scope in scopes.OrderBy(s => s.PeriodKeyValue))
        {
            written += await formulas
                .RecalculateAllAsync(scope.DocumentId, new PeriodKey(scope.PeriodKeyValue), ct)
                .ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>Прив'язки методологій до таблиць документа.</summary>
    /// <remarks>
    /// Прив'язка живе в <c>cfg.CalculationBinding</c> і посилається на
    /// <c>TableDefId</c> — опис таблиці. Прогін працює з ЕКЗЕМПЛЯРАМИ, тому
    /// опис розгортається в екземпляри цього документа й періоду.
    /// </remarks>
    private async Task<List<CalculationBindingRef>> BindingsAsync(
        RecalculationRequest request, CancellationToken ct)
    {
        var instances = await db.TableInstances
            .AsNoTracking()
            .Where(i => i.DocumentId == request.DocumentId
                        && (request.PeriodKey == null || i.PeriodKeyValue == request.PeriodKey))
            .Take(MaxBindings)
            .Select(i => new InstanceRow(i.Id, i.TableDefId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            return [];
        }

        var tableDefIds = instances.Select(i => i.TableDefId).Distinct().ToList();

        var bindings = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && tableDefIds.Contains(b.TableDefId))
            .Take(MaxBindings)
            .Select(b => new BindingRow(b.TableDefId, b.MethodologyId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return instances
            .SelectMany(i => bindings
                .Where(b => b.TableDefId == i.TableDefId)
                .Select(b => new CalculationBindingRef(i.Id, b.MethodologyId)))
            .Distinct()
            .ToList();
    }

    /// <summary>Усі документи проєкту — для перерахунку «на весь проєкт».</summary>
    /// <remarks>
    /// Q-151/Q-162: саме цей перелік замінює «нуль документів» на «усі
    /// документи проєкту», коли <c>DocumentId</c> у завданні — 0 або менше.
    /// </remarks>
    private async Task<IReadOnlyList<long>> ProjectDocumentIdsAsync(int projectId, CancellationToken ct)
        => await db.Documents
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId)
            .Select(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Розбирає завдання черги.</summary>
    /// <remarks>
    /// Payload приходить як анонімний об'єкт від use-case і як JSON із черги —
    /// обидва шляхи мусять читатися однаково, інакше задача працювала б
    /// у тесті й падала в проді.
    /// </remarks>
    private static RecalculationRequest Parse(object? payload)
    {
        if (payload is RecalculationRequest typed)
        {
            return typed;
        }

        var json = payload as string ?? JsonSerializer.Serialize(payload);

        return JsonSerializer.Deserialize<RecalculationRequest>(json, PayloadOptions)
               ?? throw new InvalidOperationException(
                   "Завдання перерахунку не розбирається: невідома форма payload.");
    }

    /// <summary>Екземпляр таблиці документа.</summary>
    private sealed record InstanceRow(long Id, int TableDefId);

    /// <summary>Прив'язка методології до опису таблиці.</summary>
    private sealed record BindingRow(int TableDefId, int MethodologyId);

    /// <summary>Документ і період, для яких рахуються формули шаблону.</summary>
    private sealed record ScopeRow(long DocumentId, int PeriodKeyValue);

    /// <summary>Прогрес однієї фази: шкала зсунута й стиснута, повідомлення назване.</summary>
    /// <param name="inner">Канал прогресу задачі.</param>
    /// <param name="floor">Скільки відсотків уже пройдено до цієї фази.</param>
    /// <param name="ceiling">Скільки відсотків відведено на кінець цієї фази.</param>
    /// <param name="phase">Ім'я фази — префікс кожного повідомлення.</param>
    /// <remarks>
    /// ⛔ Голе «40 %» не означає нічого: у прогоні дві фази, і перше, на що
    /// дивиться той, хто розбирає повільний прогін, — у якій він саме зараз.
    /// Оркестратор методологій свого місця в загальній шкалі не знає і знати
    /// не має — переклад його 0…100 у <c>floor…ceiling</c> живе тут.
    /// <para>
    /// ⚠ Q-151/Q-162: один документ (<c>floor=0, ceiling=100</c>) дає той
    /// самий результат, що й раніше жорстко зашите <c>100</c>, — навмисно, щоб
    /// не зламати наявний тест точних відсотків. Кілька документів ділять
    /// шкалу на рівні відрізки <c>floor…ceiling</c> між собою.
    /// </para>
    /// </remarks>
    private sealed class PhaseProgress(IJobProgress inner, int floor, int ceiling, string phase)
        : IJobProgress
    {
        /// <inheritdoc />
        public Task ReportAsync(int percent, string? message, CancellationToken ct)
            => inner.ReportAsync(
                floor + (Math.Clamp(percent, 0, 100) * (ceiling - floor) / 100),
                string.IsNullOrWhiteSpace(message) ? phase : $"{phase}: {message}",
                ct);
    }
}

/// <summary>Завдання на перерахунок.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="DocumentId">Документ; нуль — усі документи проєкту.</param>
/// <param name="PeriodKey">Період; <c>null</c> — повний рік.</param>
/// <param name="TriggeredByUserId">Хто запустив; <c>null</c> — за розкладом.</param>
public sealed record RecalculationRequest(
    int ProjectId, long DocumentId, int? PeriodKey, int? TriggeredByUserId);
