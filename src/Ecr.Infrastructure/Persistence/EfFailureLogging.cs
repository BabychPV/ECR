using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Рівні журналу для збоїв, які застосунок сам мапить у відповідь клієнтові (E1-06).
/// </summary>
/// <remarks>
/// ⛔ EF Core пише збій команди (<c>RelationalEventId.CommandError</c>) і збій збереження
/// (<c>CoreEventId.SaveChangesFailed</c>) рівнем <c>Error</c> ЗАВЖДИ — навіть коли застосунок зараз перетворить його
/// на звичайний 409 (гонка за унікальним індексом, <c>UnitOfWork.TryMapDuplicateKey</c>) чи 503. Журнал подій Windows
/// пише від <c>Warning</c>, тож кожна така гонка лишала там запис «Error» без жодного збою сервера, а справжні помилки
/// тонули серед них. Тепер ці дві події — <c>Warning</c>: слід лишається (і в журналі подій, і у файловому), але не як
/// помилка. Справді необроблений збій однаково пише <c>ExceptionHandlingMiddleware</c> рівнем <c>Error</c> зі стеком
/// (<c>LogUnhandled</c>), а фонові задачі — власним журналом.
/// </remarks>
internal static class EfFailureLogging
{
    /// <summary>Знижує <c>CommandError</c> і <c>SaveChangesFailed</c> з <c>Error</c> до <c>Warning</c>.</summary>
    /// <param name="options">Налаштування контексту.</param>
    public static TBuilder ConfigureEfFailureLogLevels<TBuilder>(this TBuilder options)
        where TBuilder : DbContextOptionsBuilder
    {
        ArgumentNullException.ThrowIfNull(options);

        options.ConfigureWarnings(warnings => warnings.Log(
            (CoreEventId.SaveChangesFailed, LogLevel.Warning),
            (RelationalEventId.CommandError, LogLevel.Warning)));

        return options;
    }
}
