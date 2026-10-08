using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Validation;

/// <summary>
/// D-PS: обов'язкове поле шапки (<c>HeaderFieldDef.IsRequired</c>) мусить бути заповнене до подання.
/// Єдине місце правила для <c>SubmitSheetHandler</c> (відмова) і <c>ValidateDocumentHandler</c> («Перевірити»).
/// </summary>
/// <remarks>
/// ⚠ Лише КОДИ порожніх полів, жодного значення шапки. Видимості (Column/Table) у шапки немає, тому
/// <see cref="HiddenValidationIssues.CanSee"/> пропускає це повідомлення читачеві будь-якого доступу.
/// </remarks>
public static class RequiredHeaderCheck
{
    /// <summary>Ключ каталогу відмови.</summary>
    public const string MessageKey = "err.ECR-HDR-0422.requiredAtSubmit";

    /// <summary>Запасний текст — той самий, що рядок <c>en</c> у <c>09-seed.sql</c>.</summary>
    public const string FallbackText =
        "The sheet cannot be submitted: required header field(s) are empty: {headerFieldCodes}. Fill them in the document header and submit again.";

    /// <summary>Живі обов'язкові поля шапки; порожній список — значень читати не треба.</summary>
    public static List<HeaderFieldDef> RequiredFields(IEnumerable<HeaderFieldDef> fields)
        => [.. fields.Where(f => !f.IsDeleted && f.IsRequired)];

    /// <summary>Коди обов'язкових полів без значення, у порядку шапки.</summary>
    public static List<string> EmptyCodes(
        IReadOnlyList<HeaderFieldDef> required, IReadOnlyDictionary<int, DocumentHeaderValueData> values)
        => [.. required
            .Where(f => !values.TryGetValue(f.Id, out var value) || IsBlank(value))
            .OrderBy(f => f.Ordinal).ThenBy(f => f.Code, StringComparer.Ordinal)
            .Select(f => f.Code)];

    /// <summary>Повідомлення «Перевірити»: одна помилка рівня документа з усіма кодами.</summary>
    public static ValidationMessage ToMessage(IReadOnlyList<string> codes)
    {
        var joined = string.Join(", ", codes);
        var parameters = new Dictionary<string, string> { ["headerFieldCodes"] = joined };
        return new ValidationMessage(
            ValidationSeverity.Error,
            ErrorCodes.HeaderValueInvalid,
            FallbackText.Replace("{headerFieldCodes}", joined, StringComparison.Ordinal),
            TableDefId: 0,
            RowKey: null,
            ColumnCode: null,
            BlocksSave: false,
            MessageKey: MessageKey,
            Params: parameters);
    }

    /// <summary>Чи це повідомлення шапки (не належить таблиці, а не знеособлене зауваження).</summary>
    public static bool IsHeaderMessage(ValidationMessage message)
        => message.TableDefId == 0 && message.RuleCode == ErrorCodes.HeaderValueInvalid;

    private static bool IsBlank(DocumentHeaderValueData value)
        => value.IsEmpty || (value.ValueString is { } text && string.IsNullOrWhiteSpace(text));
}
