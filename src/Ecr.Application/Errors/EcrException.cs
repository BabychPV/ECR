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
/// <remarks>
/// ⚠ <paramref name="details"/> з'явився пізніше за сам тип (`Q-341`), і це не
/// вирівнювання сигнатур із сусідами заради симетрії. Подробиця відмови
/// локалізується РІВНО тоді, коли виняток несе <c>Details["messageKey"]</c>
/// (<c>ExceptionHandlingMiddleware.ResolveGenericMessageAsync</c>). Доки
/// параметра не було, у 404 не існувало місця, куди покласти ключ, тож КОЖЕН
/// «не знайдено» доїжджав користувачеві готовим українським реченням
/// незалежно від мови інтерфейсу — мови, якої серед мов продукту
/// (<c>en</c>/<c>ru</c>/<c>kz</c>) немає взагалі.
///
/// ⚠ Параметр НЕОБОВ'ЯЗКОВИЙ: жоден із наявних кидків не переписується
/// механічно. Речення українською лишається запасним шляхом — резолвер
/// повертається до нього, коли ключа немає в каталозі.
/// </remarks>
/// <param name="errorCode">Код помилки.</param>
/// <param name="message">Запасне речення (показується, коли ключа немає).</param>
/// <param name="details">
/// <c>messageKey</c> плюс сирі підстановки рядками; решта полів їде клієнтові
/// структурою.
/// </param>
public sealed class NotFoundException(
    string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);

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
/// ⚠ Власний код заведено: <c>ECR-INT-0502</c>
/// (<see cref="Ecr.Domain.Errors.ErrorCodes.SourceAuthenticationRefused"/>).
/// Клієнт і журнал розрізняють «джерело лежить» (<c>ECR-INT-0503</c>, минає
/// само) і «нас не пускають» (не минає ніколи).
///
/// ⛔ Номер не <c>0401</c>, як пропонував крок: цифри означають НАШ статус
/// відповіді, а <c>401</c> сказав би клієнтові «увійдіть» — хоча не пускають
/// не його, а нас.
/// </remarks>
/// <param name="errorCode">Код помилки.</param>
/// <param name="message">Текст без стека (ФВ-6.11).</param>
/// <param name="details">Подробиці для журналу.</param>
public sealed class SourceAuthenticationException(
    string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);
