// src/Ecr.Application/Integration/SourceCredentialRejection.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Integration;

/// <summary>
/// Перша відмова автентифікації збереженим паролем (R-03): мітка в базі, мітка на завантаженій сутності й жодного
/// винятку назовні — спільне для обох адаптерів.
/// </summary>
public static partial class SourceCredentialRejection
{
    /// <summary>Ставить мітку відмови; збій запису мітки лише журналюється (L10).</summary>
    /// <param name="rejections">Порт мітки; <c>null</c> — лише мітка в пам'яті.</param>
    /// <param name="source">Відстежувана сутність прогону.</param>
    /// <param name="logger">Журнал.</param>
    /// <remarks>
    /// ⛔ L2: мітка й на сутності в пам'яті — повторне взяття з'єднання в тому самому прогоні (оренда, повтор збирача)
    /// бачить її й до драйвера не йде. ⛔ L10: збій запису мітки не підміняє відмову автентифікації — її тип і далі
    /// вирішує, що бачить людина й що робить наздоганяння.
    /// </remarks>
    public static async Task ReportAsync(IDataSourceCredentialRejections? rejections, DataSource source, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);

        if (rejections is not null)
        {
            try
            {
                await rejections.ReportRejectedAsync(source, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Збій мітки не має підміняти відмову автентифікації.
            catch (Exception e)
#pragma warning restore CA1031
            {
                LogReportFailed(logger, source.Code, SafeLogException.Of(e, null));
            }
        }

        source.MarkCredentialsRejected(DateTime.UtcNow);
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Мітку відмови облікових даних джерела {SourceCode} не записано: {Error}.")]
    private static partial void LogReportFailed(ILogger logger, string sourceCode, SafeLogException error);
}
