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
/// <remarks>
/// ⚠ Не <c>sealed</c> заради одного підтипу —
/// <see cref="SourceResponseTooLargeException"/>: він мусить лишатися
/// <see cref="BusinessRuleException"/> для кожного наявного <c>catch</c> і арма
/// статусу, але відрізнятися ТИПОМ для політики ретраїв фонових задач.
/// </remarks>
public class BusinessRuleException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : EcrException(errorCode, message, details);

/// <summary>
/// Зовнішнє джерело віддало відповідь, більшу за межу збирача
/// (<c>err.ECR-INT-0503.piWebApiResponseTooLarge</c>).
/// </summary>
/// <remarks>
/// ⛔ Окремий тип, а не <see cref="BusinessRuleException"/> з кодом
/// <c>ECR-INT-0503</c>: той самий код означає «джерело недоступне», і саме його
/// фонові задачі повторюють (30 + 60 + 120 с). Завелика відповідь від повтору
/// не меншає — той самий запит дасть той самий обсяг, тож три ретраї лише
/// ~3.5 хв показували б «виконується» замість причини (Н-Л4).
/// <para>
/// ⚠ Код лишається <c>ECR-INT-0503</c>, ключ — той самий: контракт, сід і
/// клієнт (<c>collectionRunMessage.ts</c>) не змінюються.
/// </para>
/// </remarks>
public sealed class SourceResponseTooLargeException(string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : BusinessRuleException(errorCode, message, details);

/// <summary>
/// Джерело НЕ НАЛАШТОВАНЕ до кінця: немає запиту, рядок з'єднання не читається, адресу відхиляє політика.
/// </summary>
/// <remarks>
/// ⛔ Окремий тип, а не <see cref="BusinessRuleException"/> з кодом <c>ECR-INT-0503</c> (b14 source-test-flert,
/// AC-3): проба з'єднання показувала цю відмову як «джерело недоступне або відповіло помилкою», хоча до джерела
/// ніхто не ходив — бракувало налаштування в нас. Код і ключі лишаються ТІ САМІ (жоден <c>catch</c>, арм статусу
/// чи текст не змінюється), розрізняє тип.
/// </remarks>
/// <param name="errorCode">Код помилки.</param>
/// <param name="message">Текст без стека.</param>
/// <param name="details">Подробиці; <c>messageKey</c> — той самий, що й досі.</param>
/// <param name="catalogOnly">
/// Бракує ЛИШЕ запиту каталогу сутностей: з'єднання може бути цілим (джерело FLERT для складу газу каталогу
/// не має). Проба тоді перевіряє саме з'єднання (<c>IExternalConnectionCheck</c>).
/// </param>
public sealed class SourceNotConfiguredException(
    string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null, bool catalogOnly = false)
    : BusinessRuleException(errorCode, message, details)
{
    /// <summary>Бракує лише запиту каталогу сутностей.</summary>
    public bool CatalogOnly { get; } = catalogOnly;
}

/// <summary>
/// З'єднання з джерелом відкрилося, а НАЛАШТОВАНИЙ запит упав на боці джерела (немає об'єкта, прав, синтаксис).
/// </summary>
/// <remarks>
/// ⛔ Окремий тип (b14 source-test-flert, AC-3): сирий <c>SqlException</c> із виконання запиту проба зараховувала
/// до «недоступне», і людина лагодила мережу, коли треба було лагодити запит. Сирий текст джерела — лише в журналі
/// сервера (кореляція в повідомленні), до клієнта не йде (S3).
/// </remarks>
/// <param name="errorCode">Код помилки.</param>
/// <param name="message">Текст без тексту джерела.</param>
/// <param name="details">Подробиці; <c>messageKey</c>.</param>
public sealed class SourceQueryFailedException(
    string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null)
    : BusinessRuleException(errorCode, message, details);

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
