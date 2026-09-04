namespace Ecr.Domain.Abstractions;

/// <summary>
/// Порушення доменного інваріанта. Несе код із каталогу помилок, щоб
/// повідомлення дійшло до клієнта машинно-читаним, а не текстом.
/// </summary>
public sealed class DomainException(string errorCode, string message) : Exception(message)
{
    /// <summary>Код із <see href="02-contracts.md#error-codes">каталогу помилок</see>.</summary>
    public string ErrorCode { get; } = errorCode;
}
