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
/// ⚠ Межа (D-577): документ річної розкладки рахується по МІСЯЦЯХ правки (місячна колонка — свій місяць, не-місячна —
/// якір). Правка загальних даних якоря, що змінює й інші місяці, лишає їх застарілими до «Перерахувати» — чип це покаже.
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
    IMetadataCache? metadata = null)
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
    public async Task<int> OnEditedAsync(
        long documentId, IReadOnlyCollection<int> periodKeys, IReadOnlySet<int>? templateVersionIds, CancellationToken ct)
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

                return any ? await EnqueueAsync(documentId, keys, ct).ConfigureAwait(false) : 0;
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

            return await EnqueueAsync(documentId, keys, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEnqueueFailed(_logger, documentId, ex);
            return 0;
        }
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
