using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Calculations;

/// <summary>
/// Оркеструє прогін розрахунку: підбирає методології, будує порядок, виконує
/// модулі, записує результати.
/// </summary>
/// <remarks>
/// **Бюджет повного річного перерахунку — ≤ 10 хвилин** (ПРД-13). Базова лінія
/// чинної системи — 20 хвилин, тому «не гірше» тут не працює: потрібне
/// щонайменше дворазове прискорення. Це і диктує архітектуру нижче.
/// </remarks>
/// <param name="resolver">Резолвер версій методологій.</param>
/// <param name="periods">Сховище періодів.</param>
/// <param name="scopeFactory">Фабрика scope гілок пакета (<c>Q-249</c>).</param>
/// <param name="limits">
/// Ліміти прогону (ФВ-9.8, секція <c>Calculations</c>); <c>null</c> — типові
/// (<see cref="CalculationLimits"/>).
/// </param>
public sealed class CalculationOrchestrator(
    MethodologyResolver resolver,
    IPeriodStore periods,
    IServiceScopeFactory scopeFactory,
    CalculationLimits? limits = null) : ICalculationRunner
{
    /// <summary>
    /// Скільки методологій одного пакета виконувати одночасно і скільки комірок
    /// входу дозволено одній прив'язці (ФВ-9.8, <c>D-205</c>).
    /// </summary>
    /// <remarks>
    /// Обмеження паралелізму обов'язкове: прогін не має з'їдати p95 операторів,
    /// які в цей час заповнюють форми. Ізоляція від інтерактивного піку — вимога,
    /// а не побажання (ПРД-13). ⚠ Перевіряється тут, на створенні, а не на
    /// першому прогоні: недійсний ліміт — вада складання, а не даних.
    /// </remarks>
    private readonly CalculationLimits _limits = (limits ?? new CalculationLimits()).EnsureValid();

    /// <summary>
    /// Кеш знімків довідників за прогоном (RT-23a, FEATURE-REGISTRY-TABLES §5.7).
    /// </summary>
    /// <remarks>
    /// ⚠ За прогоном, а не за викликом: задача перерахунку кличе <see cref="RunAsync(long,
    /// long, PeriodKey, IReadOnlyList{CalculationBindingRef}, IJobProgress, CancellationToken)"/>
    /// на КОЖЕН документ × період того самого прогону, і склад потоку, спільний для
    /// всіх документів, читався б стільки разів, скільки їх. Ключ знімка всередині —
    /// (довідники, бізнес-дата, момент), тож різні періоди не змішуються.
    /// </remarks>
    private readonly ConcurrentDictionary<long, RegistrySnapshotCache> _registries = new();

    /// <inheritdoc />
    /// <param name="calculationRunId">Прогін, створений use-case.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="bindings">Прив'язки методологій до таблиць документа.</param>
    /// <param name="progress">Канал прогресу для UI.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Профіль по модулях — заповнюється завжди (J-1).</returns>
    /// <remarks>
    /// ⚠ Без моменту довідники читаються на поточний момент (<c>null</c>). Задача
    /// перерахунку кличе перевантаження з моментом і передає
    /// <c>CalculationRun.RegistryAsOfUtc</c> (RT-23a).
    /// </remarks>
    public Task<ModuleProfile> RunAsync(
        long calculationRunId,
        long documentId,
        PeriodKey periodKey,
        IReadOnlyList<CalculationBindingRef> bindings,
        IJobProgress progress,
        CancellationToken ct)
        => RunAsync(calculationRunId, documentId, periodKey, bindings, progress, registryAsOfUtc: null, ct);

    /// <summary>Виконує прогін із заданим моментом знімка довідників.</summary>
    /// <param name="calculationRunId">Прогін, створений use-case.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="bindings">Прив'язки методологій до таблиць документа.</param>
    /// <param name="progress">Канал прогресу для UI.</param>
    /// <param name="registryAsOfUtc">
    /// <c>CalculationRun.RegistryAsOfUtc</c>: усі довідники прогону читаються
    /// <c>FOR SYSTEM_TIME AS OF</c> цього моменту (<c>D-158</c>, AC-7); <c>null</c> —
    /// поточні дані.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Профіль по модулях — заповнюється завжди (J-1).</returns>
    /// <exception cref="ArgumentException">
    /// Той самий прогін уже йшов з іншим моментом: два моменти в одному прогоні дали б
    /// змішаний стан, який не відтворює жоден повтор.
    /// </exception>
    public async Task<ModuleProfile> RunAsync(
        long calculationRunId,
        long documentId,
        PeriodKey periodKey,
        IReadOnlyList<CalculationBindingRef> bindings,
        IJobProgress progress,
        DateTime? registryAsOfUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(progress);

        var profile = new ModuleProfile();
        if (bindings.Count == 0)
        {
            return profile;
        }

        var registries = _registries.GetOrAdd(calculationRunId, _ => new RegistrySnapshotCache(registryAsOfUtc));
        if (registries.RegistryAsOfUtc != registryAsOfUtc)
        {
            throw new ArgumentException(
                $"Прогін {calculationRunId} уже читає довідники станом на "
                + $"{registries.RegistryAsOfUtc:O}, а не на {registryAsOfUtc:O}.",
                nameof(registryAsOfUtc));
        }

        var onDate = await PeriodDateAsync(documentId, periodKey, ct).ConfigureAwait(false);

        // 1. Версія методології — за ДАТОЮ ПЕРІОДУ, не за «поточною» (ФВ-9.3).
        var resolved = new List<ResolvedBinding>(bindings.Count);
        foreach (var binding in bindings)
        {
            var descriptor = await resolver
                .ResolveVersionAsync(binding.MethodologyId, onDate, ct)
                .ConfigureAwait(false);

            if (descriptor is not null)
            {
                resolved.Add(new ResolvedBinding(binding.TableInstanceId, descriptor));
            }
        }

        // 2. Пакети незалежних методологій. ⛔ Порожні залежності — не
        //    «поки немає в схемі» (`calc.MethodologyDependency` є), а інваріант:
        //    одна методологія може читати іншу лише через `!Code` в імпорт.
        //    ✎ HSE301 L: заборону аудиту A3 (ключ
        //    `publish.problem.importedFormulaNotEvaluated`, прибраний із
        //    каталогу) знято — формулу бібліотеки модуль обчислює в контексті
        //    рядка викликача, тож порядок прогону між методологіями на число
        //    не впливає, а ребра `calc.MethodologyDependency` потрібні лише
        //    для інвалідації (ФВ-9.10). Тому набір лягає в один пакет і йде
        //    паралельно; якщо колись з'явиться залежність за результатом —
        //    сюди мусять прийти реальні ребра.
        var batches = CalculationPlan.Build(
            resolved.Select(r => new CalculationNode(r.Descriptor.MethodologyVersionId, [])).ToList());

        var byVersion = resolved.ToLookup(r => r.Descriptor.MethodologyVersionId);
        var done = 0;

        // L-4: хто що закрив — для діагностики «No matching rule … row N».
        var rowMatches = new ConcurrentBag<RowMatchSummary>();

        foreach (var batch in batches)
        {
            // 3. ⚠ Усередині пакета — ПАРАЛЕЛЬНО. Послідовний прогін у 10
            //    хвилин не вкладається: методології одного пакета незалежні за
            //    побудовою, і виконувати їх по черзі означає платити сумою там,
            //    де можна платити максимумом.
            var measured = new ConcurrentBag<(string Module, TimeSpan Elapsed, int Rows)>();

            await Parallel.ForEachAsync(
                batch.MethodologyVersionIds,
                new ParallelOptions { MaxDegreeOfParallelism = _limits.MaxParallelism, CancellationToken = ct },
                async (versionId, token) =>
                {
                    // 3a. ⛔ Q-249: `MethodologyResolver`/`CalculationInputBuilder`/
                    //     `CalculationOutputWriter`/`ICalculationModule` — Scoped
                    //     і зрештою обгортають один `EcrDbContext`, який НЕ є
                    //     потокобезпечним. Використання полів оркестратора (з
                    //     батьківського scope запиту) напряму тут означало б, що
                    //     всі паралельні гілки пакета б'ють у той самий
                    //     DbContext одночасно — саме на цьому сценарії
                    //     (≥2 незалежні гілки) падало
                    //     `InvalidOperationException: A second operation
                    //     started on this context before a previous operation
                    //     completed`. Власний scope на гілку → власний
                    //     DbContext на гілку, а паралелізм пакета (заради
                    //     бюджету 10 хв, ПРД-13) лишається як є.
                    using var scope = scopeFactory.CreateScope();
                    var scopedResolver = scope.ServiceProvider.GetRequiredService<MethodologyResolver>();
                    var scopedInputBuilder = scope.ServiceProvider.GetRequiredService<CalculationInputBuilder>();
                    var scopedOutputWriter = scope.ServiceProvider.GetRequiredService<CalculationOutputWriter>();
                    var scopedModules = scope.ServiceProvider.GetRequiredService<IEnumerable<ICalculationModule>>();

                    foreach (var binding in byVersion[versionId])
                    {
                        var stat = await ExecuteAsync(
                            scopedResolver, scopedModules, scopedInputBuilder, scopedOutputWriter,
                            calculationRunId, documentId, periodKey, binding, registries,
                            _limits.MaxInputCellsPerBinding, rowMatches, token).ConfigureAwait(false);

                        measured.Add(stat);
                    }
                }).ConfigureAwait(false);

            foreach (var (module, elapsed, rowCount) in measured)
            {
                profile.Record(module, elapsed, rowCount);
            }

            done += batch.MethodologyVersionIds.Count;

            await progress
                .ReportKeyAsync(
                    done * 100 / resolved.Count,
                    "jobs.batchProgress",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["ordinal"] = (batch.Ordinal + 1).ToString(CultureInfo.InvariantCulture),
                        ["total"] = batches.Count.ToString(CultureInfo.InvariantCulture),
                    },
                    ct)
                .ConfigureAwait(false);
        }

        profile.RecordUnmatched(UnmatchedRowsOf(rowMatches));

        return profile;
    }

    /// <summary>Підсумок зіставлення рядків таблиці з правилами однієї прив'язки.</summary>
    private sealed record RowMatchSummary(
        long TableInstanceId, IReadOnlyCollection<string> MatchedKeys, IReadOnlyList<string>? AllRowKeys);

    /// <summary>
    /// Рядки, яким не підійшло жодне правило жодної прив'язки своєї таблиці (L-4).
    /// </summary>
    /// <remarks>
    /// ⚠ Саме «жодної прив'язки таблиці»: дві методології з доповняльними правилами на одну
    /// таблицю (CO2 / NOx) не дають діагностики на рядки, які закрила хоч одна. Версія без правил
    /// (<c>AllRowKeys = null</c>) рядків не бачить і діагностики не дає. Номер рядка — позиція
    /// за <c>TableRow.Id</c>, з 1; значень комірок діагностика не містить.
    /// </remarks>
    private static List<UnmatchedRow> UnmatchedRowsOf(IEnumerable<RowMatchSummary> summaries)
    {
        var unmatched = new List<UnmatchedRow>();
        foreach (var table in summaries.GroupBy(s => s.TableInstanceId).OrderBy(g => g.Key))
        {
            var all = table.Select(s => s.AllRowKeys).FirstOrDefault(keys => keys is not null);
            if (all is null)
            {
                continue;
            }

            var matched = table.SelectMany(s => s.MatchedKeys).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < all.Count; i++)
            {
                if (!matched.Contains(all[i]))
                {
                    unmatched.Add(new UnmatchedRow(table.Key, i + 1, all[i]));
                }
            }
        }

        return unmatched;
    }

    /// <summary>Виконує одну прив'язку і повертає її внесок у профіль.</summary>
    /// <remarks>
    /// Приймає <paramref name="scopedResolver"/>/<paramref name="scopedModules"/>/
    /// <paramref name="scopedInputBuilder"/>/<paramref name="scopedOutputWriter"/>
    /// явними параметрами, а не полями оркестратора: метод викликається
    /// всередині паралельної гілки пакета (`Parallel.ForEachAsync`), і кожна
    /// гілка мусить отримати екземпляри зі СВОГО DI-scope (Q-249) — інакше
    /// кілька гілок паралельно б'ють у той самий Scoped `EcrDbContext`.
    /// </remarks>
    private static async Task<(string Module, TimeSpan Elapsed, int Rows)> ExecuteAsync(
        MethodologyResolver scopedResolver,
        IEnumerable<ICalculationModule> scopedModules,
        CalculationInputBuilder scopedInputBuilder,
        CalculationOutputWriter scopedOutputWriter,
        long calculationRunId,
        long documentId,
        PeriodKey periodKey,
        ResolvedBinding binding,
        RegistrySnapshotCache registries,
        int maxInputCells,
        ConcurrentBag<RowMatchSummary> rowMatches,
        CancellationToken ct)
    {
        var module = scopedModules.FirstOrDefault(m => m.CanHandle(binding.Descriptor));
        if (module is null)
        {
            // ⛔ Немає модуля, здатного виконати рівень методології — це
            // помилка конфігурації, а не порожній результат. Найчастіша
            // причина: рівень 2 (скрипти), який не зареєстрований без дозволу
            // ІБ (K-1). Мовчазний нуль тут виглядав би як «викидів немає».
            throw new Domain.Abstractions.DomainException(
                "ECR-CALC-0422",
                $"Немає модуля для методології {binding.Descriptor.Code} "
                + $"рівня {binding.Descriptor.Level}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.noModule",
                    ["code"] = binding.Descriptor.Code,
                    ["level"] = $"{binding.Descriptor.Level}",
                });
        }

        var outcome = await scopedResolver
            .MatchRowsDetailedAsync(binding.Descriptor.MethodologyVersionId, binding.TableInstanceId, ct)
            .ConfigureAwait(false);
        IReadOnlyList<string> rowKeys = [.. outcome.Matches.Select(m => m.RowKey)];
        rowMatches.Add(new RowMatchSummary(binding.TableInstanceId, rowKeys, outcome.AllRowKeys));

        // 4. Входи ПАКЕТНО: один запит на методологію × період, не N на рядок.
        var inputs = await scopedInputBuilder
            .BuildAsync(binding.TableInstanceId, rowKeys, periodKey, binding.Descriptor, ct)
            .ConfigureAwait(false);

        // 4a. ⛔ Бюджет обсягу прив'язки (ФВ-9.8, D-205): ДО підготовки модуля і
        //     до першого рядка, тож надмірна прив'язка не породжує ні виходів, ні
        //     трейсу, ні запису — `outputWriter` не викликається. Міра —
        //     кількість комірок входу (аргументів усіх рядків), а не
        //     `GC.GetTotalMemory`: купа — спільна для всього процесу й
        //     недетермінована, а ліміт мусить давати ту саму відповідь на тих
        //     самих даних.
        //
        //     ⚠ Бюджет НЕ рахує знімок довідників (RT-23a): його вантажить
        //     `PrepareAsync` нижче, він кешується на ПРОГІН і спільний між
        //     гілками й прив'язками, тож приписати його одній прив'язці не можна.
        //     Його обсяг лишається без межі — [debt].
        //
        //     ⚠ Зріз таблиці на цей момент уже прочитано (`BuildAsync`): бюджет
        //     обмежує виконання, виходи, трейс і запис, а не саме читання.
        //     Порційне читання за rowKeys — [debt].
        var inputCells = 0L;
        foreach (var input in inputs)
        {
            inputCells += input.Arguments.Count;
        }

        if (inputCells > maxInputCells)
        {
            throw new Domain.Abstractions.DomainException(
                Domain.Errors.ErrorCodes.CalculationInputTooLarge,
                $"Прив'язка методології {binding.Descriptor.Code} до таблиці {binding.TableInstanceId} "
                + $"має {inputCells} комірок входу — більше за бюджет {maxInputCells} "
                + $"({CalculationLimits.SectionName}:{nameof(CalculationLimits.MaxInputCellsPerBinding)}).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-4222.inputCellsOverBudget",
                    ["code"] = binding.Descriptor.Code,
                    ["tableInstanceId"] = binding.TableInstanceId.ToString(CultureInfo.InvariantCulture),
                    ["cells"] = inputCells.ToString(CultureInfo.InvariantCulture),
                    ["limit"] = maxInputCells.ToString(CultureInfo.InvariantCulture),
                });
        }

        var stopwatch = Stopwatch.StartNew();
        var outputs = new List<CalculationOutput>(inputs.Count);

        // 5. ⛔ Склад версії методології і межі періоду читаються РАЗ НА
        //    ПРИВ'ЯЗКУ (`CAL-06`), а не на рядок. Доти кожен виклик
        //    `ExecuteAsync` сам ходив по формули, речовини й виходи версії та
        //    по межі періоду — тобто на 300 рядків таблиці припадало 1200
        //    запитів по відповідь, яка в межах прив'язки не змінюється за
        //    побудовою (версія опублікована, період — той самий).
        //
        //    ⚠ Лише коли є що рахувати: порожній набір рядків не має коштувати
        //    жодного походу в базу — рівно так поводився й цикл до зміни.
        if (inputs.Count > 0)
        {
            // ⚠ RT-23a: знімок довідників — з кешу ПРОГОНУ (§5.7), спільного для
            // всіх прив'язок і паралельних гілок: завантажується один раз на
            // (довідники, бізнес-дата, момент), а не на прив'язку.
            var prepared = await module
                .PrepareAsync(binding.Descriptor, documentId, periodKey, registries, ct)
                .ConfigureAwait(false);

            foreach (var input in inputs)
            {
                // 6. Проміжні значення живуть у пам'яті воркера: модуль нічого
                //    не пише і не читає з бази між рядками.
                outputs.Add(await module.ExecuteAsync(prepared, input, ct).ConfigureAwait(false));
            }
        }

        stopwatch.Stop();

        await scopedOutputWriter
            .WriteAsync(calculationRunId, outputs, binding.Descriptor.TraceLevel, ct)
            .ConfigureAwait(false);

        return (module.Code, stopwatch.Elapsed, inputs.Count);
    }

    /// <summary>Останній день періоду — дата, на яку резолвиться версія.</summary>
    /// <remarks>
    /// ⛔ Межі беруться з реальних меж періоду документа, а НЕ виводяться
    /// арифметикою з <c>PeriodKey</c>. <c>PeriodKey = Year*100 + Sequence</c>
    /// (R-A6), і для квартального/річного проєкту <c>Sequence</c> — це номер
    /// кварталу/року, не місяць: попередня версія цього методу читала
    /// <c>Sequence % 100</c> як номер місяця (`Math.Clamp(..., 1, 12)`) для
    /// БУДЬ-ЯКОГО <c>PeriodKind</c> — для кварталу 2 це резолвило версію
    /// методології на 28 лютого замість справжнього кінця кварталу (30/31
    /// червня), без жодної помилки, що це впіймала б (D-112, той самий клас
    /// дефекту, якого <see cref="GenericCalculationModule.PeriodAsync"/> у
    /// цьому ж проєкті явно уникає тим самим способом — через реальні межі
    /// періоду, а не арифметику ключа).
    /// </remarks>
    private async Task<DateOnly> PeriodDateAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        // ⛔ Спершу — чи це взагалі період, і лише потім похід у сховище.
        // Раніше цієї перевірки не було, і `RecalculationJob` для прогону «на
        // весь рік» (`PeriodKey = null`) віддавав сюди `new PeriodKey(0)`.
        // Далі йшов `FindPeriodBoundsAsync(documentId, 0)` → `null` →
        // `ECR-PRD-0404` «періоду 0 для документа N не існує». Правда — і НЕ
        // ПРО ТЕ: суб'єктом помилки виглядав документ із поламаним календарем,
        // тоді як зламаний був викликач, який не розклав рік на періоди. Той,
        // хто розбирав нічне падіння, шукав би дані, а не однорядкову ваду в
        // задачі. Нуль сюди більше не доходить (задача ітерує періоди), але
        // межа мусить бути на боці оркестратора: він єдиний знає, що йому
        // потрібен САМЕ період, а не будь-яке ціле.
        //
        // ⚠ Саме `ArgumentOutOfRangeException`, а не доменний виняток із кодом:
        // це порушення контракту ВИКЛИКАЧЕМ, а не бізнес-відмова, яку показують
        // людині. Доменний код означав би, що така відповідь передбачена
        // сценарієм, — тут передбаченого сценарію немає, є вада в коді.
        if (!periodKey.IsValid)
        {
            throw new ArgumentOutOfRangeException(
                nameof(periodKey),
                periodKey.Value,
                "Прогін методологій отримав ключ, який не є періодом (очікується Рік*100 + Номер, "
                + "напр. 202601). Найімовірніша причина — спроба порахувати методології ОДРАЗУ ЗА "
                + "ВЕСЬ РІК одним викликом: версія методології резолвиться за датою періоду "
                + "(ФВ-9.3), тож рік треба розкласти на періоди й викликати прогін для кожного "
                + "окремо.");
        }

        var bounds = await periods
            .FindPeriodBoundsAsync(documentId, periodKey.Value, ct)
            .ConfigureAwait(false)
            ?? throw new Domain.Abstractions.DomainException(
                "ECR-PRD-0404",
                $"Періоду {periodKey.Value} для документа {documentId} не існує: "
                + "дату резолвінгу методології обчислити нема з чого.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0404.periodForDocument",
                    ["periodKey"] = periodKey.Value.ToString(CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });

        return bounds.PeriodEnd;
    }

    /// <summary>Прив'язка з уже підібраною версією.</summary>
    private sealed record ResolvedBinding(long TableInstanceId, MethodologyDescriptor Descriptor);
}


