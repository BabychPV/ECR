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
/// (<see cref="Invalidate"/>), а підйом робить цей перехоплювач на <c>TransactionCommitted</c>; відкат позначку
/// знімає, епоха не рухається. Піднятий ДО коміту, лічильник встиг би закешувати ще старі дані під новою епохою.
/// Поза транзакцією (автокоміт) підйом негайний.
///
/// ⚠ Гарячий шлях PATCH: <see cref="Invalidate"/> - O(1), без блокувань і без звернень до БД
/// (<c>ConditionalWeakTable</c> + <c>Interlocked</c>).
///
/// ⚠ Епоха живе в ПРОЦЕСІ (singleton): за кількох вузлів API правка на одному вузлі дійде до лічильника іншого лише
/// через TTL (<see cref="DocumentListSummaryStore.StaleCountsTtl"/>) - TTL лишається страховкою.
///
/// ⛔ Безпека: епоха глобальна, але ключ кешу й далі містить хеш scope читача; кеш зберігає лише числа,
/// тож інвалідація нічого не розкриває.
/// </remarks>
public sealed class StaleCountsEpoch : DbTransactionInterceptor
{
    private static readonly object Marker = new();

    private readonly ConditionalWeakTable<DbContext, object> _pending = [];
    private long _value;

    /// <summary>Поточна епоха.</summary>
    public long Value => Interlocked.Read(ref _value);

    /// <summary>Піднімає епоху (O(1), без блокувань).</summary>
    public void Bump() => Interlocked.Increment(ref _value);

    /// <summary>
    /// Позначає, що контекст змінив "застарілість": у транзакції - підйом на коміті, поза нею - негайно.
    /// </summary>
    /// <param name="db">Контекст запису.</param>
    public void Invalidate(DbContext? db)
    {
        if (db is null)
        {
            return;
        }

        if (db.Database.CurrentTransaction is null)
        {
            Bump();
            return;
        }

        _pending.AddOrUpdate(db, Marker);
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
        if (context is not null && _pending.Remove(context) && bump)
        {
            Bump();
        }
    }
}
