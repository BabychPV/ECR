using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Епоха кешу лічильників «результати застаріли» (<c>DocumentListSummaryStore</c>): входить у ключ кешу, тож її
/// зміна робить усі записи недосяжними. ЗАГЛУШКА [TEST]-коміту: інвалідації ще немає.
/// </summary>
public sealed class StaleCountsEpoch : DbTransactionInterceptor
{
    private long _value;

    /// <summary>Поточна епоха.</summary>
    public long Value => Interlocked.Read(ref _value);

    /// <summary>Піднімає епоху (O(1), без блокувань).</summary>
    public void Bump() => Interlocked.Increment(ref _value);

    /// <summary>Заглушка: нічого не робить.</summary>
    /// <param name="db">Контекст запису.</param>
    public void Invalidate(DbContext? db) => _ = db;
}
