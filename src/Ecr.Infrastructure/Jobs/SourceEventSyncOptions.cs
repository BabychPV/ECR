// src/Ecr.Infrastructure/Jobs/SourceEventSyncOptions.cs
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Jobs;

/// <summary>D-464: як синк подій читає вікно повної звірки.</summary>
public enum SourceEventQueryChunk
{
    /// <summary>Одне читання на все вікно (як до D-464) — один запит на шаблон і сторінку.</summary>
    None,

    /// <summary>
    /// Вікно читається помісячно (календарні місяці UTC): кожен місяць — своє читання зі своєю стелею сторінок.
    /// </summary>
    Month,
}

/// <summary>Налаштування синку подій (<see cref="SourceEventSyncJob"/>, D-464).</summary>
/// <param name="MaxEventPages">
/// Скільки сторінок (по <c>SourceEventQuery.DefaultMaxEvents</c> подій) дочитувати за одне читання шаблону; понад
/// це читання обрізане — пізніші події цим прогоном не синхронізуються, видалення зниклих вимкнено.
/// </param>
/// <param name="EventQueryChunk">Нарізка читання вікна; <see cref="SourceEventQueryChunk.None"/> — без нарізки.</param>
/// <remarks>
/// ⚠ Нарізка — не заміна повної звірки: вікно лишається тим самим (усі відкриті періоди документів мапінгів), лише
/// читається кількома запитами. Подія, що перетинає межу місяця (і незакрита), приходить у кількох шматках і
/// зливається за <c>EventId</c>; обрізаний чи з відмовою будь-який шматок робить обрізаним увесь прогін — тоді ні
/// <c>Missing</c>, ні видалення.
/// </remarks>
public sealed record SourceEventSyncOptions(
    int MaxEventPages = SourceEventSyncOptions.DefaultMaxEventPages,
    SourceEventQueryChunk EventQueryChunk = SourceEventQueryChunk.None)
{
    /// <summary>Ключ стелі сторінок.</summary>
    public const string MaxEventPagesKey = "Collection:MaxEventPages";

    /// <summary>Ключ нарізки читання подій.</summary>
    public const string EventQueryChunkKey = "PiSqlClient:EventQueryChunk";

    /// <summary>Типова стеля — 10 сторінок, тобто 20 000 подій на шаблон за читання (L3-07).</summary>
    public const int DefaultMaxEventPages = 10;

    /// <summary>Типові значення.</summary>
    public static SourceEventSyncOptions Default { get; } = new();

    /// <summary>
    /// З конфігурації; порожнє чи недійсне — типове (недійсне зупиняє старт Api раніше: <c>EcrConfigurationValidation</c>).
    /// </summary>
    /// <param name="configuration">Конфігурація.</param>
    public static SourceEventSyncOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var pages = int.TryParse(
                        configuration[MaxEventPagesKey],
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed)
                    && parsed > 0
            ? parsed
            : DefaultMaxEventPages;
        var chunk = Enum.TryParse<SourceEventQueryChunk>(configuration[EventQueryChunkKey], ignoreCase: true, out var mode)
                    && Enum.IsDefined(mode)
            ? mode
            : SourceEventQueryChunk.None;

        return new SourceEventSyncOptions(pages, chunk);
    }
}
