// src/Ecr.Application/Permits/PermitErrors.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits;

/// <summary>Відмови екземплярів дозволів (<c>ECR-PRM-*</c>, Permits PM-04).</summary>
/// <remarks>
/// ⚠ Ключ каталогу - повним літералом у точці створення винятку: сторож <c>ErrorTitleCatalogTests</c> звіряє з сідом саме
/// літерали.
/// </remarks>
internal static class PermitErrors
{
    /// <summary><c>404 ECR-PRM-0404 permit</c>: дозволу немає (або заборона на реєстр його шапки).</summary>
    /// <param name="permitId">Ідентифікатор з запиту.</param>
    internal static NotFoundException NotFound(long permitId)
        => new(
            ErrorCodes.PermitTypeNotFound,
            $"Дозволу {permitId} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0404.permit",
                ["id"] = permitId.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary><c>422 ECR-PRM-0422 validityOrder</c>: кінець не пізніший за початок.</summary>
    /// <param name="from">Початок.</param>
    /// <param name="to">Кінець.</param>
    internal static BusinessRuleException ValidityOrder(DateOnly from, DateOnly to)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"Дозвіл має закінчуватися після початку: {from:yyyy-MM-dd} — {to:yyyy-MM-dd}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0422.validityOrder",
                ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });

    /// <summary><c>422 ECR-PRM-0422 numberRequired</c>.</summary>
    internal static BusinessRuleException NumberRequired()
        => new(
            ErrorCodes.PermitTypeInvalid,
            "Номер дозволу обов'язковий.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PRM-0422.numberRequired" });

    /// <summary><c>422 ECR-PRM-0422 numberTooLong</c>.</summary>
    /// <param name="max">Найбільша довжина.</param>
    internal static BusinessRuleException NumberTooLong(int max)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"Номер дозволу довший за {max} символів.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0422.numberTooLong",
                ["max"] = max.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary><c>422 ECR-PRM-0422 locationTooLong</c>.</summary>
    /// <param name="max">Найбільша довжина.</param>
    internal static BusinessRuleException LocationTooLong(int max)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"Розташування довше за {max} символів.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0422.locationTooLong",
                ["max"] = max.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary><c>422 ECR-PRM-0422 typeNotPublished</c>: дозвіл заводять лише за опублікованим типом.</summary>
    /// <param name="typeCode">Код типу.</param>
    internal static BusinessRuleException TypeNotPublished(string typeCode)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"Тип дозволу «{typeCode}» ще не опублікований: дозвіл за ним завести не можна.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-0422.typeNotPublished",
                ["typeCode"] = typeCode,
            });

    /// <summary><c>409 ECR-PRM-4095 number</c>: за типом уже є дозвіл із цим номером і початком.</summary>
    /// <param name="typeCode">Код типу.</param>
    /// <param name="number">Номер.</param>
    /// <param name="validFrom">Початок чинності.</param>
    /// <param name="existingId">Дозвіл, який уже є.</param>
    internal static ConcurrencyConflictException Duplicate(string typeCode, string number, DateOnly validFrom, long existingId)
        => new(
            ErrorCodes.PermitDuplicate,
            $"Дозвіл «{number}» типу «{typeCode}» з початком {validFrom:yyyy-MM-dd} уже існує (Id {existingId}).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-PRM-4095.number",
                ["typeCode"] = typeCode,
                ["number"] = number,
                ["validFrom"] = validFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["id"] = existingId.ToString(CultureInfo.InvariantCulture),
            });
}
