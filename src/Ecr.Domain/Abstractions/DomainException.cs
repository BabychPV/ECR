namespace Ecr.Domain.Abstractions;

/// <summary>
/// Порушення доменного інваріанта. Несе код із каталогу помилок, щоб
/// повідомлення дійшло до клієнта машинно-читаним, а не текстом.
/// </summary>
/// <remarks>
/// ⛔ <paramref name="details"/> — навмисно опційний, а не новий обов'язковий
/// параметр: конструктор викликається майже сотнею місць у Domain, і жоден
/// не повинен ламатися через додавання цього поля. Значення потрібне лише
/// там, де повідомлення будується в Domain (без доступу до
/// <c>IUiStringCatalog</c> — цей шар його НІКОЛИ не отримає) і має дійти до
/// клієнта локалізованим: <c>Details["messageKey"]</c> — ключ каталогу,
/// решта пар — підстановки <c>{ім'я}</c> в його шаблон
/// (<c>ExceptionHandlingMiddleware.LocalizedDetailAsync</c>, той самий
/// механізм, що й ECR-AUTH-0403, узагальнений під довільний ключ).
/// </remarks>
public sealed class DomainException(
    string errorCode, string message, IReadOnlyDictionary<string, object?>? details = null) : Exception(message)
{
    /// <summary>Код із <see href="02-contracts.md#error-codes">каталогу помилок</see>.</summary>
    public string ErrorCode { get; } = errorCode;

    /// <summary>Структуровані подробиці для клієнта — насамперед ключ локалізації.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; } = details;
}
