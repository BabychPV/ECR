// src/Ecr.Api/Errors/EcrProblemDetails.cs

using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Errors;

/// <summary>
/// Помилка API. Розширює стандартний <see cref="ProblemDetails"/> кодом і
/// машинними подробицями: клієнт має розрізняти причини, а не парсити текст.
/// </summary>
public sealed class EcrProblemDetails : ProblemDetails
{
    /// <summary>Код із каталогу <see href="02-contracts.md#error-codes">#error-codes</see>.</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Наскрізний ідентифікатор запиту для звірки з логами.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Подробиці, специфічні для коду: конфлікти, перелік заборонених комірок тощо.</summary>
    public IReadOnlyDictionary<string, object?>? Extensions2 { get; init; }
}
