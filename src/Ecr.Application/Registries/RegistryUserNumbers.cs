// src/Ecr.Application/Registries/RegistryUserNumbers.cs
using System.Globalization;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Registries;

/// <summary>Числове поле запису, текст якого за мовою користувача не читається однозначно.</summary>
/// <param name="FieldCode">Код поля.</param>
/// <param name="DataType">Тип поля.</param>
/// <param name="Value">Текст, як його надіслали.</param>
/// <param name="Reading">Прочитання (<see cref="NumberTextKind.Ambiguous"/> або <see cref="NumberTextKind.NotNumber"/>).</param>
public sealed record RegistryNumberTextError(string FieldCode, CellDataType DataType, string Value, NumberTextReading Reading)
{
    /// <summary>Ключ каталогу «не число».</summary>
    public const string NotNumberKey = "err.ECR-REG-0422.valueNotNumber";

    /// <summary>Ключ каталогу неоднозначного числа: число є, лише двозначне — «не число» вводило б в оману.</summary>
    public const string AmbiguousKey = "err.ECR-REG-0422.valueAmbiguousSeparator";

    /// <summary>Ключ каталогу цієї відмови.</summary>
    public string MessageKey => Reading.Kind == NumberTextKind.Ambiguous ? AmbiguousKey : NotNumberKey;

    /// <summary>Параметри відмови: поле, значення, тип і — для неоднозначного — причина з обома прочитаннями.</summary>
    public IReadOnlyDictionary<string, object?> Details()
    {
        var details = new Dictionary<string, object?>
        {
            ["messageKey"] = MessageKey,
            ["fieldCode"] = FieldCode,
            ["value"] = Value,
            ["dataType"] = DataType.ToString(),
        };

        if (Reading.Kind == NumberTextKind.Ambiguous)
        {
            details["reason"] = CellValueReader.AmbiguousSeparator;
            details["asGroup"] = Reading.AsGroup;
            details["asDecimal"] = Reading.AsDecimal;
        }

        return details;
    }

    /// <summary>Відмова одиночного запиту: <c>422 ECR-REG-0422</c>.</summary>
    public BusinessRuleException ToException()
        => new(
            "ECR-REG-0422",
            Reading.Kind == NumberTextKind.Ambiguous
                ? $"Поле «{FieldCode}»: роздільник у «{Value}» неоднозначний (розряди чи десятковий)."
                : $"Поле «{FieldCode}»: «{Value}» не є числом.",
            new Dictionary<string, object?>(Details())
            {
                ["messageKey"] = Reading.Kind == NumberTextKind.Ambiguous ? AmbiguousKey : NotNumberKey,
            });
}

/// <summary>
/// Числа, що прийшли в записі довідника ТЕКСТОМ від людини, — за культурою її мови, на межі
/// Application, до <see cref="RegistryEntryWriter"/>.
/// </summary>
/// <remarks>
/// ⛔ Дефект: ручний upsert з <c>"12,5"</c> у числовому полі доходив до
/// <c>RegistryValue.Set</c> → <c>Convert.ToDecimal(…, Invariant)</c> (<c>NumberStyles.Number</c>
/// — кома як розряди) і лягав як <c>125</c>. Тепер користувацький обробник (upsert, пакет RT-14)
/// читає текст <see cref="CultureNumberReader"/> за <c>ICurrentUser.Language</c>, а writer отримує
/// вже <see cref="decimal"/>.
/// <para>
/// ⚠ НЕ в <c>RegistryValue.Set</c> і НЕ в writer'і: синк (S7) пише машинний інваріантний запис, і
/// культура користувача, під яким іде фонова задача, його не стосується.
/// </para>
/// <para>
/// ⚠ JSON-число, <c>decimal</c>, <c>bool</c>, <c>null</c> і нечислові поля — без змін; невідоме
/// поле теж пропускається — його назве writer (<c>unknownFields</c>).
/// </para>
/// </remarks>
public static class RegistryUserNumbers
{
    /// <summary>Значення з текстом чисел, розібраним за <paramref name="culture"/>.</summary>
    /// <param name="definition">Опис довідника (типи полів).</param>
    /// <param name="values">Значення з запиту.</param>
    /// <param name="culture">Культура користувача (<see cref="NumberCulture.ForLanguage"/>).</param>
    /// <param name="error">Перше поле, текст якого не число або неоднозначний.</param>
    /// <returns>Нові значення; за помилки — <c>null</c>.</returns>
    public static IReadOnlyDictionary<string, object?>? TryParse(
        RegistryDef definition,
        IReadOnlyDictionary<string, object?>? values,
        CultureInfo culture,
        out RegistryNumberTextError? error)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(culture);

        error = null;
        if (values is null || values.Count == 0)
        {
            return values ?? new Dictionary<string, object?>();
        }

        var numeric = definition.Fields
            .Where(f => f.DataType is CellDataType.Int or CellDataType.Decimal)
            .ToDictionary(f => f.Code, f => f.DataType, StringComparer.Ordinal);

        var result = new Dictionary<string, object?>(values.Count, StringComparer.Ordinal);
        foreach (var (code, raw) in values)
        {
            if (!numeric.TryGetValue(code, out var dataType) || CellValueReader.Normalize(raw) is not string text)
            {
                result[code] = raw;
                continue;
            }

            var reading = CultureNumberReader.Read(text, culture);
            if (reading.Kind != NumberTextKind.Number)
            {
                error = new RegistryNumberTextError(code, dataType, text, reading);
                return null;
            }

            result[code] = reading.Value;
        }

        return result;
    }

    /// <summary>Те саме, що <see cref="TryParse"/>, але помилка — виняток <c>422</c>.</summary>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0422</c>, <see cref="RegistryNumberTextError.MessageKey"/>.</exception>
    public static IReadOnlyDictionary<string, object?> Parse(
        RegistryDef definition, IReadOnlyDictionary<string, object?>? values, CultureInfo culture)
        => TryParse(definition, values, culture, out var error) ?? throw error!.ToException();
}
