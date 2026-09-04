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
