namespace Ecr.Adapters.PiAf;

/// <summary>Налаштування транспорту PI SQL Client (D-462).</summary>
/// <param name="CommandTimeoutSeconds">
/// Тайм-аут однієї команди ODBC, секунд; <c>null</c> або не додатне — <see cref="DefaultCommandTimeoutSeconds"/>.
/// </param>
/// <remarks>
/// ⚠ Тайм-аут діє на КОЖНУ спробу команди: читання повторюється до <see cref="PiSqlClientDataSource.MaxAttempts"/>
/// разів, тож найгірша сторінка — близько трьох тайм-аутів плюс паузи, і це має вміщатися в ліміт прогону
/// (<see cref="CollectionRunner.DefaultMaxRunDuration"/>) з запасом на інші сторінки.
/// </remarks>
public sealed record PiSqlClientOptions(int? CommandTimeoutSeconds)
{
    /// <summary>Ключ конфігурації тайм-ауту команди.</summary>
    public const string CommandTimeoutSecondsKey = "PiSqlClient:CommandTimeoutSeconds";

    /// <summary>
    /// Типовий тайм-аут команди — 60 с: удвічі довше за типові 30 с драйвера (сторінка — до 5 000 рядків RTQP
    /// під навантаженням), і три спроби — 3 хв, п'ята частина ліміту прогону.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 60;
}
