using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Епоха кешу лічильників «результати застаріли» (<c>DocumentListSummaryStore</c>): входить у ключ кешу, тож її
/// зміна робить усі наявні записи недосяжними (вони дожиють до TTL і зникнуть).
/// </summary>
/// <remarks>
/// ⛔ Піднімається ПІСЛЯ коміту транзакції, що змінила "застарілість": запис комірок
/// (<c>AuditWriter.WriteCellChangesAsync</c>) і перемикання актуального прогону
/// (<c>CalculationResultStore.SwitchCurrentRunAsync</c>) лише ПОЗНАЧАЮТЬ контекст
/// (<see cref="Invalidate(DbContext?, IEnumerable{int}?)"/>), а підйом робить цей перехоплювач на <c>TransactionCommitted</c>; відкат позначку
/// знімає, епоха не рухається. Піднятий ДО коміту, лічильник встиг би закешувати ще старі дані під новою епохою.
/// Поза транзакцією (автокоміт) підйом негайний.
///
/// ⚠ Гарячий шлях PATCH: <see cref="Invalidate(DbContext?, IEnumerable{int}?)"/> - O(1), без блокувань і без звернень до БД
/// (<c>ConditionalWeakTable</c> + <c>Interlocked</c>).
///
/// ⚠ Епоха живе в ПРОЦЕСІ (singleton): за кількох вузлів API правка на одному вузлі дійде до лічильника іншого лише
/// через TTL (<see cref="DocumentListSummaryStore.StaleCountsTtl"/>) - TTL лишається страховкою.
///
/// ⛔ Безпека: епоха не залежить від читача, ключ кешу й далі містить хеш scope читача; кеш зберігає лише числа,
/// тож інвалідація нічого не розкриває.
///
/// ⛔ AN-108 / P2-01: епоха — ПО ПЕРІОДУ (<see cref="ValueFor"/>), а не одна на процес. Застарілість періоду P
/// рахується лише з правок і прогонів періоду P (<c>StaleResultsQuery</c>), а глобальна епоха за 100 редакторів
/// росла 10–20 разів на секунду: кеш лічильників не влучав ніколи. Запис без відомого періоду (повний прогін,
/// <c>PeriodKey = null</c>) піднімає ГЛОБАЛЬНУ складову, що входить у кожен період.
/// </remarks>
public sealed class StaleCountsEpoch : DbTransactionInterceptor
{
    private readonly ConditionalWeakTable<DbContext, Pending> _pending = [];
    private readonly ConcurrentDictionary<int, long> _periods = new();
    private long _global;
    private long _value;

    /// <summary>Скільки разів епоха рухалась загалом (будь-який період або глобально); для діагностики й тестів.</summary>
    public long Value => Interlocked.Read(ref _value);

    /// <summary>
    /// Епоха періоду для ключа кешу: глобальна складова + складова періоду. Обидві лише ростуть, тож будь-який
    /// підйом дає значення, якого цей період ще не мав.
    /// </summary>
    /// <param name="periodKey">Період лічильника.</param>
    public long ValueFor(int periodKey)
        => Interlocked.Read(ref _global) + (_periods.TryGetValue(periodKey, out var own) ? own : 0);

    /// <summary>Піднімає ГЛОБАЛЬНУ епоху — всі періоди (O(1), без блокувань).</summary>
    public void Bump()
    {
        Interlocked.Increment(ref _global);
        Interlocked.Increment(ref _value);
    }

    /// <summary>Піднімає епоху одного періоду.</summary>
    /// <param name="periodKey">Період.</param>
    public void Bump(int periodKey)
    {
        _periods.AddOrUpdate(periodKey, 1, static (_, v) => v + 1);
        Interlocked.Increment(ref _value);
    }

    /// <summary>
    /// Позначає, що контекст змінив "застарілість" УСІХ періодів: у транзакції - підйом на коміті, поза нею - негайно.
    /// </summary>
    /// <param name="db">Контекст запису.</param>
    public void Invalidate(DbContext? db) => Invalidate(db, periodKeys: null);

    /// <summary>
    /// Позначає, що контекст змінив "застарілість" названих періодів (<c>null</c> - усіх): у транзакції - підйом
    /// на коміті, поза нею - негайно.
    /// </summary>
    /// <param name="db">Контекст запису.</param>
    /// <param name="periodKeys">Періоди змін; <c>null</c> - невідомо, піднімається глобальна епоха.</param>
    public void Invalidate(DbContext? db, IEnumerable<int>? periodKeys)
    {
        if (db is null)
        {
            return;
        }

        if (db.Database.CurrentTransaction is null)
        {
            if (periodKeys is null)
            {
                Bump();
                return;
            }

            foreach (var period in periodKeys.Distinct())
            {
                Bump(period);
            }

            return;
        }

        // ⚠ Контекст EF не потокобезпечний: позначки одного контексту пишуться з одного потоку.
        var pending = _pending.GetValue(db, static _ => new Pending());
        if (periodKeys is null)
        {
            pending.All = true;
        }
        else
        {
            pending.Periods.UnionWith(periodKeys);
        }
    }

    /// <inheritdoc />
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Flush(eventData.Context, bump: true);
        base.TransactionCommitted(transaction, eventData);
    }

    /// <inheritdoc />
    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context, bump: true);
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    /// <inheritdoc />
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Flush(eventData.Context, bump: false);
        base.TransactionRolledBack(transaction, eventData);
    }

    /// <inheritdoc />
    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Flush(eventData.Context, bump: false);
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    private void Flush(DbContext? context, bool bump)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending))
        {
            return;
        }

        _pending.Remove(context);
        if (!bump)
        {
            return;
        }

        if (pending.All)
        {
            Bump();
            return;
        }

        foreach (var period in pending.Periods)
        {
            Bump(period);
        }
    }

    /// <summary>Позначки контексту до коміту.</summary>
    private sealed class Pending
    {
        /// <summary>Невідомий період - глобальний підйом.</summary>
        public bool All { get; set; }

        /// <summary>Періоди змін.</summary>
        public HashSet<int> Periods { get; } = [];
    }
}
