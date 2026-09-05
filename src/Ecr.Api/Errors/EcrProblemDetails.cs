// src/Ecr.Api/Errors/EcrProblemDetails.cs

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Errors;

/// <summary>
/// Помилка API. Розширює стандартний <see cref="ProblemDetails"/> кодом і
/// машинними подробицями: клієнт має розрізняти причини, а не парсити текст.
/// </summary>
/// <remarks>
/// ⛔ Типізовані властивості <b>не серіалізуються</b>. У тілі відповіді код і
/// подробиці лежать ПЛОСКО — так вимагає <c>application/problem+json</c>
/// (RFC 9457 §3.2: члени-розширення є полями верхнього рівня). Без цієї
/// заборони той самий `errorCode` виходив у JSON двічі, а поруч зʼявлялося
/// беззмістовне для клієнта поле `extensions2` (`A7-15`) — форма, за якою
/// генератор клієнтських типів описав би контракт помилки неправильно.
/// </remarks>
public sealed class EcrProblemDetails : ProblemDetails
{
    /// <summary>Код із каталогу <see href="02-contracts.md#error-codes">#error-codes</see>.</summary>
    [JsonIgnore]
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>Наскрізний ідентифікатор запиту для звірки з логами.</summary>
    [JsonIgnore]
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>Подробиці, специфічні для коду: конфлікти, перелік заборонених комірок тощо.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, object?>? Extensions2 { get; init; }
}
