using Microsoft.Extensions.Logging;

namespace Ecr.Api.Health;

/// <summary>
/// Попередження на старті: база часових поясів ОС не знає про перехід Казахстану
/// на UTC+5 (F-4).
/// </summary>
/// <remarks>
/// ⛔ ЗАПУСК НЕ БЛОКУЄТЬСЯ. Це лише запис рівня Warning у журнал: сервер із
/// необновленою базою поясів має піднятися й працювати, а рішення — оновлювати ОС
/// чи ні — за оператором. Будь-який збій самої перевірки теж лише пишеться в лог;
/// виняток звідси зупинив би старт через діагностику, а не через несправність.
///
/// ⚠ Те саме питання повторює <see cref="TimeZoneDatabaseHealthCheck"/> на
/// <c>/health/ready</c> (картка <c>tzdata</c>); тут — щоб причина лягла в журнал
/// один раз на старті, а не на кожну пробу балансувальника.
/// </remarks>
public static partial class TimeZoneDatabaseStartup
{
    /// <summary>Пише попередження, якщо база поясів застаріла; ніколи не кидає.</summary>
    /// <param name="app">Зібраний застосунок.</param>
    public static void ReportTimeZoneDatabase(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ecr.Startup");

        try
        {
            var provider = app.Services.GetRequiredService<ITimeZoneOffsetProvider>();
            var drift = KazakhstanTimeZoneReference.FindDrift(provider);

            if (drift.Count > 0)
            {
                LogStale(logger, string.Join(", ", drift.Select(d => d.Describe())));
            }
        }
#pragma warning disable CA1031 // Діагностика не має права зупиняти старт (див. ⛔ вище).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "База часових поясів ОС застаріла: {Zones}. Казахстан на UTC+5 з 2024-03-01, тож межі періодів "
                  + "проєктів на цих поясах зсунуті. Оновіть tzdata (Linux) або накопичувальне оновлення Windows "
                  + "із часовими поясами. Запуск не блокується.")]
    private static partial void LogStale(ILogger logger, string zones);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Не вдалося перевірити базу часових поясів ОС; запуск триває.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
