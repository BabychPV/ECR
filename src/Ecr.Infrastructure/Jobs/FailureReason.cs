// src/Ecr.Infrastructure/Jobs/FailureReason.cs
using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Мовонейтральна причина відмови для ПАРАМЕТРА локалізованого тексту (журнал покриття, відмова задачі).
/// </summary>
/// <remarks>
/// ⛔ Y4-05: <c>Exception.Message</c> власних винятків продукту (<see cref="EcrException"/>, <see cref="DomainException"/>)
/// - український текст розробника, а не дані джерела. Підставлений параметром у шаблон каталогу
/// (<c>coverageEvents.eventWriteFailed</c>, <c>err.ECR-UOM-0422.boundaryConversionFailed</c>), він давав англійське,
/// російське чи казахське речення з українським шматком усередині. Тут - ключ каталогу причини (<c>messageKey</c>
/// винятку), а без нього - код помилки: так само, як <c>CoverageDetails.CellRejected</c> несе код без тексту.
/// </remarks>
internal static class FailureReason
{
    /// <summary>Ключ каталогу причини; за його відсутності - код помилки; для чужих винятків - лише назва типу.</summary>
    /// <param name="error">Виняток.</param>
    public static string Of(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var (code, details) = error switch
        {
            EcrException ecr => (ecr.ErrorCode, ecr.Details),
            DomainException domain => (domain.ErrorCode, domain.Details),
            _ => (error.GetType().Name, null),
        };

        return details is not null
               && details.TryGetValue("messageKey", out var key)
               && key is string text
               && !string.IsNullOrWhiteSpace(text)
            ? text
            : code;
    }
}
