// src/Ecr.Application/Errors/EcrException.cs
namespace Ecr.Application.Errors;

/// <summary>
/// Помилка прикладного рівня з кодом. Використовується замість голих
/// <see cref="InvalidOperationException"/>: код потрапляє в API і в логи.
/// </summary>
public class EcrException : Exception
{
    public string ErrorCode { get; }
    public IReadOnlyDictionary<string, object?>? Details { get; }

    public EcrException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Details = details;
    }
}

/// <summary>Порушення бізнес-правила. HTTP 422.</summary>
public sealed class BusinessRuleException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);

/// <summary>Відмова в доступі з причиною. HTTP 403.</summary>
public sealed class AccessDeniedException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);

/// <summary>Конфлікт паралельного редагування. HTTP 409.</summary>
public sealed class ConcurrencyConflictException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);

/// <summary>Сутність не знайдена. HTTP 404.</summary>
public sealed class NotFoundException(string errorCode, string message)
    : EcrException(errorCode, message);

/// <summary>
/// Зовнішнє джерело відмовило в АВТЕНТИФІКАЦІЇ (<c>401</c>/<c>403</c>).
/// HTTP 503, як і решта відмов інтеграції.
/// </summary>
/// <remarks>
/// ⛔ Окремий тип, а не <see cref="BusinessRuleException"/> з тим самим кодом,
/// і не з педантизму: <b>наздоганяння зобов'язане його не проковтнути</b>
/// (<c>H-20</c>). Недоступне джерело збирач гасить у результат і йде далі —
/// це затримка. Відмова в автентифікації від повторення не зникає: збір
/// потрапляв у наздоганяння і <b>завершувався успішно</b>, тож неправильно
/// налаштовані облікові дані виглядали як тиша справної системи. Розрізняє їх
/// саме тип винятку — гілка <c>catch</c>, а не рядок у повідомленні.
///
/// ⚠ TODO: код помилки тимчасово <c>ECR-INT-0503</c> (недоступність джерела).
/// Потрібен власний — <c>ECR-INT-0401</c>, «джерело відмовило в
/// автентифікації»: клієнт і журнал мають розрізняти «джерело лежить» і
/// «нас не пускають», бо перше минає само, а друге ні. Завести його —
/// правка каталогу <c>02-contracts.md</c> §7 і <c>ErrorCodes</c>, чого цей
/// крок не торкається.
/// </remarks>
/// <param name="errorCode">Код помилки.</param>
/// <param name="message">Текст без стека (ФВ-6.11).</param>
/// <param name="details">Подробиці для журналу.</param>
public sealed class SourceAuthenticationException(
    string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);
