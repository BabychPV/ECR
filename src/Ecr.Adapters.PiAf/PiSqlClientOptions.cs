namespace Ecr.Adapters.PiAf;

/// <summary>Налаштування транспорту PI SQL Client (D-462, D-464).</summary>
/// <param name="CommandTimeoutSeconds">
/// Тайм-аут однієї команди ODBC, секунд; <c>null</c> або не додатне — <see cref="DefaultCommandTimeoutSeconds"/>.
/// </param>
/// <param name="EventQueryTimeoutSeconds">
/// D-464: тайм-аут команди читання ПОДІЙ (Event Frames, <c>EventQuery</c>), секунд; <c>null</c> або не додатне —
/// <see cref="DefaultEventQueryTimeoutSeconds"/>. Окремо від <paramref name="CommandTimeoutSeconds"/>: синк подій
/// перечитує ВЕСЬ відкритий (річний) період одним запитом, а сторінка тега — до 5 000 рядків.
/// </param>
/// <remarks>
/// ⚠ Тайм-аут діє на КОЖНУ спробу команди: читання повторюється до <see cref="PiSqlClientDataSource.MaxAttempts"/>
/// разів, тож найгірша сторінка — близько трьох тайм-аутів плюс паузи, і це має вміщатися в ліміт прогону
/// (<see cref="CollectionRunner.DefaultMaxRunDuration"/>) з запасом на інші сторінки. Події читає не збір, а задача
/// синку подій (ліміту прогону збору в ній немає): найгірша сторінка подій — до трьох
/// <see cref="EventQueryTimeoutSeconds"/> плюс паузи.
/// </remarks>
public sealed record PiSqlClientOptions(int? CommandTimeoutSeconds, int? EventQueryTimeoutSeconds = null)
{
    /// <summary>Ключ конфігурації тайм-ауту команди.</summary>
    public const string CommandTimeoutSecondsKey = "PiSqlClient:CommandTimeoutSeconds";

    /// <summary>Ключ конфігурації тайм-ауту читання подій (D-464).</summary>
    public const string EventQueryTimeoutSecondsKey = "PiSqlClient:EventQueryTimeoutSeconds";

    /// <summary>
    /// Типовий тайм-аут команди — 60 с: удвічі довше за типові 30 с драйвера (сторінка — до 5 000 рядків RTQP
    /// під навантаженням), і три спроби — 3 хв, п'ята частина ліміту прогону.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 60;

    /// <summary>
    /// D-464: типовий тайм-аут читання подій — 300 с. Повна звірка річного періоду — один запит на весь рік
    /// (сторінка до 2 000 подій з атрибутами), і 60 с команди тега для нього замало під навантаженням RTQP.
    /// </summary>
    public const int DefaultEventQueryTimeoutSeconds = 300;
}
