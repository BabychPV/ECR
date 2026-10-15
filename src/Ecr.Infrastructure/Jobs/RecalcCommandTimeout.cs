// src/Ecr.Infrastructure/Jobs/RecalcCommandTimeout.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Стеля таймауту SQL-команд перерахунку (<c>Database:RecalcCommandTimeoutSeconds</c>).</summary>
/// <param name="Seconds">Секунди; у складанні затиснуто в [<see cref="Min"/>, <see cref="Max"/>].</param>
/// <remarks>
/// ⛔ Чому окрема від <c>Database:CommandTimeoutSeconds</c> (60). Запис результатів великого документа
/// (десятки тисяч рядків) під зупинкою I/O (автоприріст файлу 71 с, 15.10) впирався в 60 с і падав
/// <c>SqlException -2</c> одразу в кількох прогонах. Підвищується лише для перерахунку, за шаблоном
/// <c>ArchiveJob.CommandTimeoutSeconds</c>, а читання користувача лишається швидким. Ціна — довше
/// утримання applock документа при справжньому зависанні (до 180 с замість 60 с).
/// </remarks>
public sealed record RecalcCommandTimeoutPolicy(int Seconds)
{
    /// <summary>Типове значення, с (збігається з <c>appsettings.json</c>).</summary>
    public const int Default = 180;

    /// <summary>Найменше допустиме, с.</summary>
    public const int Min = 30;

    /// <summary>Найбільше допустиме, с.</summary>
    public const int Max = 3600;

    /// <summary>Значення з конфігурації, затиснуте в допустимі межі.</summary>
    /// <param name="configured">Сире значення.</param>
    public static RecalcCommandTimeoutPolicy From(int configured)
        => new(Math.Clamp(configured, Min, Max));
}

/// <summary><see cref="IRecalcCommandTimeout"/> над контекстом поточного scope.</summary>
/// <param name="db">Контекст БД scope.</param>
/// <param name="policy">Стеля таймауту.</param>
public sealed class RecalcCommandTimeout(EcrDbContext db, RecalcCommandTimeoutPolicy policy) : IRecalcCommandTimeout
{
    /// <inheritdoc />
    public void Apply()
    {
        // ⚠ Лише підвищує: адміністратор, що задав глобальний таймаут більший за стелю перерахунку,
        // не отримує його зниження.
        var current = db.Database.GetCommandTimeout();
        if (current is null || current < policy.Seconds)
        {
            db.Database.SetCommandTimeout(policy.Seconds);
        }
    }
}
