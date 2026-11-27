// src/Ecr.Infrastructure/Jobs/EditAutoRecalculation.cs
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ D-577 (Q2, рішення людини 26.11: «автоперерахунок методологій після правок — так, тихо»): після успішного проходу
/// <see cref="FormulaRecalculationJob"/> З НАСІННЯМ (правка людини: PATCH, вставка, імпорт, додано/видалено рядок, «з
/// дозволу») — автоперерахунок методологій документа за періоди правки, з відкладенням і злиттям
/// (<see cref="ICalculationTrigger.RequestAfterEditAsync(long, PeriodKey, CancellationToken)"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Доти методології після правки не рахувались ніколи: банер «Calculation results are out of date» стояв увесь час
/// введення, і кожен раз треба було тиснути F9 (13–17 с на Land). Тепер чип показує «у черзі / рахується / актуально».
/// </para>
/// <para>
/// ⚠ Лише документ, у якого є методології (активна прив'язка <c>calc.CalculationBinding</c> до таблиці будь-якого його
/// екземпляра в періодах правки) — одним запитом; документ без методологій не ставить нічого (прогін формул лише повторив
/// би вже зроблене). Прохід лише з міжперіодних джерел (без насіння, його ставить сам перерахунок) сюди не доходить, тож
/// петлі «перерахунок → формули → перерахунок» немає.
/// </para>
/// <para>
/// ✎ Річна розкладка (рішення координатора): правка місячної колонки — свій місяць; правка НЕ-місячної колонки якоря
/// (загальні дані року) — ще й місяці року, що її читають (<see cref="WithReaderMonthsAsync"/>), тож нічого не лишається
/// застарілим мовчки.
/// </para>
/// <para>
/// ⚠ Аварійний вимикач — <c>Calculations:AutoRecalcAfterEdit:Enabled</c> (типово <c>true</c>). Збій постановки не валить
/// задачу формул (її записи вже закомічено): помилка — у журнал, а застарілість лишається видимою в чипі.
/// </para>
/// </remarks>
public sealed partial class EditAutoRecalculation(
    EcrDbContext db,
    ICalculationTrigger trigger,
    IConfiguration? configuration = null,
    ILogger<EditAutoRecalculation>? logger = null,
    // ⛔ perf («perf max», FormulaJobFixedCostTests): у DI — singleton-кеш прив'язаних таблиць і знімки версій у пам'яті:
    // задача формул документа БЕЗ методологій не звертається до бази взагалі. Без них (ручна збірка в тестах) — запит.
    BoundTablesCache? boundTables = null,
    IMetadataCache? metadata = null,
    // ✎ D-577 (річна розкладка): правка загальних (не-місячних) даних якоря будить місяці-читачі. Без реєстру — лише
    // періоди правки, як доти.
    ITemplateLayoutRegistry? layouts = null)
{
    /// <summary>Ключ аварійного вимикача.</summary>
    public const string EnabledKey = "Calculations:AutoRecalcAfterEdit:Enabled";

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Ставить автоперерахунок методологій документа за періоди правки.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKeys">Періоди, у які записала правка (насіння й похідні формул).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки періодів поставлено.</returns>
    public async Task<int> OnEditedAsync(long documentId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
        => await OnEditedAsync(documentId, periodKeys, templateVersionIds: null, ct).ConfigureAwait(false);

    /// <summary>Те саме з версіями шаблонів екземплярів правки, уже прочитаними задачею формул (ворота без бази).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKeys">Періоди правки.</param>
    /// <param name="templateVersionIds">Версії шаблонів екземплярів у цих періодах; <c>null</c> — невідомо (запит).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки періодів поставлено.</returns>
    public Task<int> OnEditedAsync(
        long documentId, IReadOnlyCollection<int> periodKeys, IReadOnlySet<int>? templateVersionIds, CancellationToken ct)
        => OnEditedAsync(documentId, periodKeys, templateVersionIds, changedColumns: null, ct);

    /// <summary>Те саме з колонками, які змінив прохід формул за періодом (річна розкладка — місяці-читачі якоря).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKeys">Періоди правки.</param>
    /// <param name="templateVersionIds">Версії шаблонів екземплярів у цих періодах; <c>null</c> — невідомо (запит).</param>
    /// <param name="changedColumns">Змінені колонки за періодом (насіння й похідні); <c>null</c> — невідомо (без розширення).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки періодів поставлено.</returns>
    public Task<int> OnEditedAsync(
        long documentId, IReadOnlyCollection<int> periodKeys, IReadOnlySet<int>? templateVersionIds,
        IReadOnlyDictionary<int, IReadOnlySet<int>>? changedColumns, CancellationToken ct)
        => OnEditedAsync(
            documentId,
            new EditedPeriods(periodKeys) { TemplateVersionIds = templateVersionIds, ChangedColumns = changedColumns },
            ct);

    /// <summary>Повний варіант: усе, що задача формул знає про правку (<see cref="EditedPeriods"/>).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="edit">Правка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки періодів поставлено.</returns>
    public async Task<int> OnEditedAsync(long documentId, EditedPeriods edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(edit.PeriodKeys);
        if (documentId == 0 || edit.PeriodKeys.Count == 0 || configuration?.GetValue(EnabledKey, true) == false)
        {
            return 0;
        }

        try
        {
            var keys = edit.PeriodKeys.Distinct().ToList();
            if (boundTables is not null && metadata is not null && edit.TemplateVersionIds is { Count: > 0 } versions)
            {
                var bound = await boundTables.GetAsync(db, ct).ConfigureAwait(false);
                var any = false;
                foreach (var versionId in versions)
                {
                    var snapshot = await metadata.GetAsync(versionId, ct).ConfigureAwait(false);
                    any |= snapshot.Sheets.SelectMany(sheet => sheet.Tables).Any(t => bound.Contains(t.Id));
                }

                if (!any)
                {
                    return 0;
                }
            }
            else
            {
                var withMethodologies = await db.TableInstances
                    .AsNoTracking()
                    .Where(i => i.DocumentId == documentId && keys.Contains(i.PeriodKeyValue)
                                && db.CalculationBindings.Any(b => b.IsActive && b.TableDefId == i.TableDefId))
                    .Select(i => i.PeriodKeyValue)
                    .Distinct()
                    .OrderBy(k => k)
                    .Take(keys.Count)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                // ⚠ Прив'язка — до таблиці, а скрипт методології читає й інші таблиці документа: період без прив'язаної таблиці,
                // але з правкою, рахується, якщо в документі є методології хоч в одному з періодів правки.
                if (withMethodologies.Count == 0)
                {
                    return 0;
                }
            }

            var periods = await WithReaderMonthsAsync(documentId, keys, edit.TemplateVersionIds, edit.ChangedColumns, ct)
                .ConfigureAwait(false);

            // ✎ Огляд H1: період, у який хук документа-джерела вже поставив ВЛАСНИЙ перерахунок (з місяцями правки, D-572), —
            // не ставимо: злиття без місяців розширило б його на весь рік, а другий запит — ще один повний прогін.
            if (edit.Claimed is { Count: > 0 } claimed)
            {
                periods = [.. periods.Where(p => !claimed.Contains(p))];
            }

            // ✎ Огляд H2 (c): важкий документ (рядків за період понад межу) — без автоперерахунку: прогін тримав би лок
            // документа так довго, що проходи формул після кожної правки відкладались би; там лишається «застаріло» + кнопка.
            periods = await WithoutHeavyAsync(documentId, periods, ct).ConfigureAwait(false);

            return await EnqueueAsync(documentId, periods, edit.ApproverSheets, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, documentId, ex);
            return 0;
        }
    }

    /// <summary>Ключ межі рядків документа за період, понад яку автоперерахунку немає (огляд H2 (c)); 0 — без межі.</summary>
    public const string MaxRowsKey = "Calculations:AutoRecalcAfterEdit:MaxRowsPerPeriod";

    /// <summary>
    /// ✎ Огляд H2 (c): типова межа — 20 000 живих рядків документа за період. ⚠ Перед увімкненням на live потрібен вимір на
    /// стенді Land (тривалість прогону місяця й відкладення проходів формул під локом документа); документи понад межу —
    /// лише «застаріло» + «Перерахувати», як доти.
    /// </summary>
    public const int DefaultMaxRowsPerPeriod = 20_000;

    private async Task<List<int>> WithoutHeavyAsync(long documentId, List<int> periods, CancellationToken ct)
    {
        var max = configuration?.GetValue(MaxRowsKey, DefaultMaxRowsPerPeriod) ?? DefaultMaxRowsPerPeriod;
        if (periods.Count == 0 || max <= 0)
        {
            return periods;
        }

        var light = new List<int>(periods.Count);
        foreach (var period in periods)
        {
            // Лічба обірвана на max+1: ціна запиту не росте з розміром документа.
            var rows = await db.TableRows
                .AsNoTracking()
                .Where(r => r.PeriodKeyValue == period && !r.IsDeleted
                            && db.TableInstances.Any(i => i.Id == r.TableInstanceId && i.PeriodKeyValue == period
                                                          && i.DocumentId == documentId))
                .Select(r => r.Id)
                .OrderBy(id => id)
                .Take(max + 1)
                .CountAsync(ct)
                .ConfigureAwait(false);
            if (rows > max)
            {
                LogHeavySkipped(_logger, documentId, period, max);
                continue;
            }

            light.Add(period);
        }

        return light;
    }

    /// <summary>
    /// ✎ D-577 (рішення координатора, річна розкладка): правка НЕ-місячної колонки якоря (загальні дані року, D-540) змінює
    /// входи кожного місяця року, що її читає, — такі місяці теж ставляться, інакше вони мовчки лишались застарілими.
    /// </summary>
    /// <remarks>
    /// Читач — за полями, які читає скрипт методологій документа (<c>calc.MethodologyReadField</c>, D-568): поле з кодом зміненої
    /// не-місячної колонки — читає; версія без запису, з маркером «читає будь-що» (<c>*</c>) чи формульна — консервативно
    /// читає. Місяці — ті, де в документа є екземпляри таблиць у році; закриті, не відкриті й затверджені відсіє сама
    /// постановка (<see cref="ICalculationTrigger.RequestAfterEditAsync(long, PeriodKey, CancellationToken)"/>). Звернення до бази — лише коли правка справді
    /// зачепила не-місячну колонку версії річної розкладки (рідко), не на кожну правку.
    /// </remarks>
    internal async Task<List<int>> WithReaderMonthsAsync(
        long documentId, List<int> keys, IReadOnlySet<int>? templateVersionIds,
        IReadOnlyDictionary<int, IReadOnlySet<int>>? changedColumns, CancellationToken ct)
    {
        if (layouts is not { HasAny: true } || metadata is null || changedColumns is null
            || templateVersionIds is not { Count: > 0 } || !templateVersionIds.Any(layouts.IsYearVersion))
        {
            return keys;
        }

        // Змінені НЕ-місячні колонки (коди) і ✎ огляд M6 — основи місячних (`X` з `X_Mmm`) за роком.
        var yearly = new Dictionary<int, (HashSet<string> Codes, HashSet<string> MonthBases)>();
        foreach (var versionId in templateVersionIds.Where(layouts.IsYearVersion))
        {
            var columns = (await metadata.GetAsync(versionId, ct).ConfigureAwait(false)).ColumnsById;
            foreach (var key in keys)
            {
                foreach (var columnId in changedColumns.GetValueOrDefault(key) ?? new HashSet<int>())
                {
                    if (!columns.TryGetValue(columnId, out var column))
                    {
                        continue;
                    }

                    if (!yearly.TryGetValue(key / 100, out var sets))
                    {
                        yearly[key / 100] = sets = (
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    }

                    if (!column.IsMonthColumn)
                    {
                        sets.Codes.Add(column.Code);
                    }
                    else if (MonthBaseOf(column.Code, column.MonthNumber) is { } monthBase)
                    {
                        sets.MonthBases.Add(monthBase);
                    }
                }
            }
        }

        if (yearly.Count == 0)
        {
            return keys;
        }

        var result = keys.ToHashSet();
        foreach (var (year, sets) in yearly)
        {
            if ((sets.Codes.Count == 0 && sets.MonthBases.Count == 0)
                || !await AnyReaderAsync(documentId, year, sets.Codes, sets.MonthBases, ct).ConfigureAwait(false))
            {
                continue;
            }

            var from = year * 100;
            var to = (year + 1) * 100;
            foreach (var month in await db.TableInstances
                         .AsNoTracking()
                         .Where(i => i.DocumentId == documentId && i.PeriodKeyValue > from && i.PeriodKeyValue < to)
                         .Select(i => i.PeriodKeyValue)
                         .Distinct()
                         .OrderBy(k => k)
                         .Take(100)
                         .ToListAsync(ct)
                         .ConfigureAwait(false))
            {
                result.Add(month);
            }
        }

        return [.. result.Order()];
    }

    /// <summary>Основа місячної колонки <c>X_Mmm</c> (<c>X</c>); <c>null</c> — код не за правилом.</summary>
    internal static string? MonthBaseOf(string code, int? month)
    {
        if (month is not { } number)
        {
            return null;
        }

        var suffix = "_M" + number.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        return code.Length > suffix.Length && code.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? code[..^suffix.Length]
            : null;
    }

    /// <summary>
    /// Чи читає скрипт хоч однієї опублікованої версії методологій документа хоч одну з не-місячних колонок <paramref name="codes"/>
    /// (консервативно: версія без запису, «будь-що», не-скрипт) чи — ✎ огляд M6 — місячне поле <paramref name="monthBases"/> за
    /// ІНШИЙ місяць (<c>row.X[m]</c>, <c>OtherMonths</c>) чи «будь-що». ⚠ Для місячної правки — лише за явним записом: без нього
    /// (версія без запису, не-скрипт) кожна правка місяця будила б увесь рік (шторм); там лишається застарілість у чипі.
    /// </summary>
    private async Task<bool> AnyReaderAsync(
        long documentId, int year, HashSet<string> codes, HashSet<string> monthBases, CancellationToken ct)
    {
        var from = year * 100;
        var to = (year + 1) * 100;
        var versions = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.Status == Domain.Enums.TemplateVersionStatus.Published
                        && db.CalculationBindings.Any(b => b.IsActive && b.MethodologyId == v.MethodologyId
                            && db.TableInstances.Any(i => i.DocumentId == documentId && i.TableDefId == b.TableDefId
                                                          && i.PeriodKeyValue > from && i.PeriodKeyValue < to)))
            .Select(v => new { v.Id, v.Engine })
            .OrderBy(v => v.Id)
            .Take(500)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return false;
        }

        if (codes.Count > 0 && versions.Any(v => v.Engine != Domain.Enums.MethodologyEngine.Script))
        {
            return true;
        }

        var ids = versions.Where(v => v.Engine == Domain.Enums.MethodologyEngine.Script).Select(v => v.Id).ToList();
        var fields = await db.MethodologyReadFields
            .AsNoTracking()
            .Where(f => ids.Contains(f.MethodologyVersionId))
            .Select(f => new { f.MethodologyVersionId, f.FieldCode, f.OtherMonths })
            .OrderBy(f => f.MethodologyVersionId)
            .Take(20_000)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byVersion = fields.ToLookup(f => f.MethodologyVersionId);

        return ids.Exists(id => (codes.Count > 0 && !byVersion[id].Any())
                                || byVersion[id].Any(f => f.FieldCode == Domain.Entities.Calculations.MethodologyReadField.AllFields
                                                          || codes.Contains(f.FieldCode)
                                                          || (f.OtherMonths && monthBases.Contains(f.FieldCode))));
    }

    private async Task<int> EnqueueAsync(
        long documentId, List<int> keys, IReadOnlyDictionary<int, IReadOnlySet<int>>? approverSheets, CancellationToken ct)
    {
        var enqueued = 0;
        foreach (var key in keys.Order())
        {
            var approver = approverSheets?.GetValueOrDefault(key);
            var jobId = approver is { Count: > 0 }
                ? await trigger.RequestAfterEditAsync(documentId, new PeriodKey(key), approver, ct).ConfigureAwait(false)
                : await trigger.RequestAfterEditAsync(documentId, new PeriodKey(key), ct).ConfigureAwait(false);
            if (jobId is not null)
            {
                enqueued++;
            }
        }

        return enqueued;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Автоперерахунок після правки документа {DocumentId} за період {Period} пропущено: рядків понад {Max} (огляд H2).")]
    private static partial void LogHeavySkipped(ILogger logger, long documentId, int period, int max);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Автоперерахунок методологій після правки документа {DocumentId} не поставлено; застарілість видно в чипі.")]
    private static partial void LogEnqueueFailed(ILogger logger, long documentId, Exception exception);
}

/// <summary>
/// ✎ D-577 (Q2, perf): таблиці з активною прив'язкою методології (<c>calc.CalculationBinding</c>) — знімок у пам'яті процесу з
/// TTL <see cref="Lifetime"/>: ворота автоперерахунку після правки без звернення до бази на кожну задачу формул.
/// </summary>
/// <remarks>⚠ Нова прив'язка діє на автоперерахунок щонайпізніше через <see cref="Lifetime"/> (ручне «Перерахувати» — одразу).</remarks>
public sealed class BoundTablesCache
{
    /// <summary>TTL знімка.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private const int MaxTables = 100_000;

    private sealed record Entry(IReadOnlySet<int> Tables, long LoadedAtTicks);

    private volatile Entry? _entry;

    /// <summary>Таблиці з активною прив'язкою: з кешу або з бази (кеш порожній чи старший за <see cref="Lifetime"/>).</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlySet<int>> GetAsync(EcrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entry = _entry;
        if (entry is not null && Environment.TickCount64 - entry.LoadedAtTicks < (long)Lifetime.TotalMilliseconds)
        {
            return entry.Tables;
        }

        var started = Environment.TickCount64;
        var tables = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive)
            .Select(b => b.TableDefId)
            .Distinct()
            .OrderBy(id => id)
            .Take(MaxTables)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var set = tables.ToHashSet();
        _entry = new Entry(set, started);
        return set;
    }
}

/// <summary>✎ D-577: що задача формул знає про правку.</summary>
/// <param name="PeriodKeys">Періоди, у які записала правка (насіння й похідні).</param>
public sealed record EditedPeriods(IReadOnlyCollection<int> PeriodKeys)
{
    /// <summary>Версії шаблонів екземплярів у цих періодах; <c>null</c> — невідомо (ворота читають базу).</summary>
    public IReadOnlySet<int>? TemplateVersionIds { get; init; }

    /// <summary>Змінені колонки за періодом; <c>null</c> — невідомо (без розширення на місяці-читачі).</summary>
    public IReadOnlyDictionary<int, IReadOnlySet<int>>? ChangedColumns { get; init; }

    /// <summary>✎ M5 (D-442): аркуші правки погоджувача за періодом — автоперерахунок не ставить їм позначку «після подання».</summary>
    public IReadOnlyDictionary<int, IReadOnlySet<int>>? ApproverSheets { get; init; }

    /// <summary>✎ Огляд H1: періоди, де власний перерахунок уже поставив хук документа-джерела (<see cref="IOwnRecalculationClaims"/>).</summary>
    public IReadOnlySet<int>? Claimed { get; init; }
}

/// <summary>
/// ✎ Огляд H1: хук, що САМ ставить власний перерахунок документа за періоди правки (документ-джерело, з місяцями правки D-572),
/// називає ці періоди, щоб автоперерахунок після правки їх не дублював і не розширював на весь рік.
/// </summary>
public interface IOwnRecalculationClaims
{
    /// <summary>Періоди документа, у які хук поставив (чи злив) власний перерахунок у межах цієї задачі формул.</summary>
    /// <param name="documentId">Документ.</param>
    public IReadOnlySet<int> OwnRecalculationQueued(long documentId);
}
