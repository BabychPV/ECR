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
/// (<see cref="ICalculationTrigger.RequestAfterEditAsync"/>).
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
    public async Task<int> OnEditedAsync(
        long documentId, IReadOnlyCollection<int> periodKeys, IReadOnlySet<int>? templateVersionIds,
        IReadOnlyDictionary<int, IReadOnlySet<int>>? changedColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);
        if (documentId == 0 || periodKeys.Count == 0 || configuration?.GetValue(EnabledKey, true) == false)
        {
            return 0;
        }

        try
        {
            var keys = periodKeys.Distinct().ToList();
            if (boundTables is not null && metadata is not null && templateVersionIds is { Count: > 0 })
            {
                var bound = await boundTables.GetAsync(db, ct).ConfigureAwait(false);
                var any = false;
                foreach (var versionId in templateVersionIds)
                {
                    var snapshot = await metadata.GetAsync(versionId, ct).ConfigureAwait(false);
                    any |= snapshot.Sheets.SelectMany(sheet => sheet.Tables).Any(t => bound.Contains(t.Id));
                }

                return any
                    ? await EnqueueAsync(
                            documentId,
                            await WithReaderMonthsAsync(documentId, keys, templateVersionIds, changedColumns, ct).ConfigureAwait(false),
                            ct)
                        .ConfigureAwait(false)
                    : 0;
            }

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

            return await EnqueueAsync(
                    documentId,
                    await WithReaderMonthsAsync(documentId, keys, templateVersionIds, changedColumns, ct).ConfigureAwait(false),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, documentId, ex);
            return 0;
        }
    }

    /// <summary>
    /// ✎ D-577 (рішення координатора, річна розкладка): правка НЕ-місячної колонки якоря (загальні дані року, D-540) змінює
    /// входи кожного місяця року, що її читає, — такі місяці теж ставляться, інакше вони мовчки лишались застарілими.
    /// </summary>
    /// <remarks>
    /// Читач — за полями, які читає скрипт методологій документа (<c>calc.MethodologyReadField</c>, D-568): поле з кодом зміненої
    /// не-місячної колонки — читає; версія без запису, з маркером «читає будь-що» (<c>*</c>) чи формульна — консервативно
    /// читає. Місяці — ті, де в документа є екземпляри таблиць у році; закриті, не відкриті й затверджені відсіє сама
    /// постановка (<see cref="ICalculationTrigger.RequestAfterEditAsync"/>). Звернення до бази — лише коли правка справді
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

        // Змінені НЕ-місячні колонки (коди) за роком.
        var yearly = new Dictionary<int, HashSet<string>>();
        foreach (var versionId in templateVersionIds.Where(layouts.IsYearVersion))
        {
            var columns = (await metadata.GetAsync(versionId, ct).ConfigureAwait(false)).ColumnsById;
            foreach (var key in keys)
            {
                foreach (var columnId in changedColumns.GetValueOrDefault(key) ?? new HashSet<int>())
                {
                    if (!columns.TryGetValue(columnId, out var column) || column.IsMonthColumn)
                    {
                        continue;
                    }

                    if (!yearly.TryGetValue(key / 100, out var codes))
                    {
                        yearly[key / 100] = codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }

                    codes.Add(column.Code);
                }
            }
        }

        if (yearly.Count == 0)
        {
            return keys;
        }

        var result = keys.ToHashSet();
        foreach (var (year, codes) in yearly)
        {
            if (!await AnyReaderAsync(documentId, year, codes, ct).ConfigureAwait(false))
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

    /// <summary>Чи читає скрипт хоч однієї опублікованої версії методологій документа хоч одну з колонок (консервативно).</summary>
    private async Task<bool> AnyReaderAsync(long documentId, int year, HashSet<string> codes, CancellationToken ct)
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

        if (versions.Any(v => v.Engine != Domain.Enums.MethodologyEngine.Script))
        {
            return true;
        }

        var ids = versions.Select(v => v.Id).ToList();
        var fields = await db.MethodologyReadFields
            .AsNoTracking()
            .Where(f => ids.Contains(f.MethodologyVersionId))
            .Select(f => new { f.MethodologyVersionId, f.FieldCode })
            .OrderBy(f => f.MethodologyVersionId)
            .Take(20_000)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byVersion = fields.ToLookup(f => f.MethodologyVersionId);

        return ids.Exists(id => !byVersion[id].Any()
                                || byVersion[id].Any(f => f.FieldCode == Domain.Entities.Calculations.MethodologyReadField.AllFields
                                                          || codes.Contains(f.FieldCode)));
    }

    private async Task<int> EnqueueAsync(long documentId, List<int> keys, CancellationToken ct)
    {
        var enqueued = 0;
        foreach (var key in keys.Order())
        {
            if (await trigger.RequestAfterEditAsync(documentId, new PeriodKey(key), ct).ConfigureAwait(false) is not null)
            {
                enqueued++;
            }
        }

        return enqueued;
    }

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
