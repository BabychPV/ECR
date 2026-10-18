// src/Ecr.Application/Permits/PermitTypeDefinitionValidator.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits;

/// <summary>
/// Перевірка визначення типу дозволу до збереження чернетки: усе, що можна сказати про склад типу, не торкаючись
/// реєстрів (Permits, PM-03). Відмова — <c>422 ECR-PRM-0422</c> з окремим <c>messageKey</c> на кожен випадок.
/// </summary>
/// <remarks>
/// ⚠ Перевірки, яким потрібна база (існування спільного довідника, одиниця виміру, дані в стовпці), тут не живуть:
/// їх робить публікація, коли реєстри змінюються. Правила виразів — рушій правил довідників при публікації.
/// ⛔ Ключ каталогу — повним літералом у кожній точці кидка.
/// </remarks>
public static class PermitTypeDefinitionValidator
{
    /// <summary>Типи стовпців, які дозволяє тип дозволу (підмножина типів реєстрів).</summary>
    private static readonly string[] DataTypes =
    [
        nameof(CellDataType.String), nameof(CellDataType.Int), nameof(CellDataType.Decimal),
        nameof(CellDataType.Bool), nameof(CellDataType.Date), nameof(CellDataType.Lookup),
    ];

    /// <summary>Назва й категорія; повертає розібрану категорію.</summary>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="category">Категорія рядком контракту.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c>.</exception>
    public static PermitCategory ValidateDetails(IReadOnlyDictionary<string, string>? name, string? category)
    {
        if (name is null || !name.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            throw Invalid("Назва типу дозволу обов'язкова.", "err.ECR-PRM-0422.nameRequired", ("item", "type"));
        }

        if (!PermitTypeCodec.TryCategory(category, out var parsed))
        {
            throw Invalid(
                $"Категорії «{category}» не існує.", "err.ECR-PRM-0422.categoryUnknown", ("category", category ?? string.Empty));
        }

        return parsed;
    }

    /// <summary>Код реєстру шапки типу має вміщатися в код довідника.</summary>
    /// <param name="typeCode">Код типу.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c> <c>registryCodeInvalid</c>.</exception>
    public static void ValidateTypeCode(string typeCode)
    {
        var registryCode = PermitHeaderFields.HeaderRegistryCode(typeCode);
        if (!EcrCode.TryCreate(registryCode, out _))
        {
            throw Invalid(
                $"Код реєстру шапки «{registryCode}» задовгий.", "err.ECR-PRM-0422.registryCodeInvalid",
                ("registryCode", registryCode));
        }
    }

    /// <summary>Перевіряє склад типу; <paramref name="published"/> — опублікований склад, який не можна зламати.</summary>
    /// <param name="typeCode">Код типу.</param>
    /// <param name="content">Склад чернетки.</param>
    /// <param name="published">Опублікований склад; <c>null</c> — тип ще не публікувався.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c>.</exception>
    public static void Validate(string typeCode, PermitTypeContent content, PermitTypeContent? published)
    {
        ArgumentNullException.ThrowIfNull(content);

        _ = ValidateDetails(content.NameL10n, content.Category);

        var tableCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in content.Tables)
        {
            if (!EcrCode.TryCreate(table.Code, out _))
            {
                throw Invalid($"Код таблиці «{table.Code}» недопустимий.", "err.ECR-PRM-0422.tableCodeInvalid", ("tableCode", table.Code ?? string.Empty));
            }

            if (!tableCodes.Add(table.Code))
            {
                throw Invalid($"Таблиця «{table.Code}» повторюється.", "err.ECR-PRM-0422.tableDuplicate", ("tableCode", table.Code));
            }

            var registryCode = PermitHeaderFields.TableRegistryCode(typeCode, table.Code);
            if (!EcrCode.TryCreate(registryCode, out _))
            {
                throw Invalid(
                    $"Код реєстру таблиці «{registryCode}» задовгий або недопустимий.",
                    "err.ECR-PRM-0422.registryCodeInvalid",
                    ("registryCode", registryCode));
            }

            if (!HasText(table.NameL10n))
            {
                throw Invalid("Назва таблиці обов'язкова.", "err.ECR-PRM-0422.nameRequired", ("item", table.Code));
            }
        }

        foreach (var table in content.Tables)
        {
            ValidateColumns(typeCode, table, tableCodes);
            ValidateRules(table.Rules);
        }

        ValidateRules(content.Rules);
        ValidateSiblingCycles(content.Tables);

        if (published is not null)
        {
            ValidateAgainstPublished(content, published);
        }
    }

    /// <summary>Додаткові вимоги публікації: хоч одна таблиця.</summary>
    /// <param name="content">Склад чернетки.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c> <c>noTables</c>.</exception>
    public static void ValidateForPublish(PermitTypeContent content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Tables.Count == 0)
        {
            throw Invalid("Тип без жодної таблиці не публікується.", "err.ECR-PRM-0422.noTables");
        }
    }

    private static void ValidateColumns(string typeCode, PermitTypeTableDto table, HashSet<string> tableCodes)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in table.Columns)
        {
            if (!EcrCode.TryCreate(column.Code, out _))
            {
                throw Invalid(
                    $"Код стовпця «{column.Code}» недопустимий.", "err.ECR-PRM-0422.columnCodeInvalid",
                    ("tableCode", table.Code), ("columnCode", column.Code ?? string.Empty));
            }

            if (string.Equals(column.Code, PermitHeaderFields.PermitFieldCode, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(
                    $"Код «{column.Code}» зарезервовано за посиланням на дозвіл.", "err.ECR-PRM-0422.columnCodeReserved",
                    ("tableCode", table.Code), ("columnCode", column.Code));
            }

            if (!codes.Add(column.Code))
            {
                throw Invalid(
                    $"Стовпець «{column.Code}» повторюється.", "err.ECR-PRM-0422.columnDuplicate",
                    ("tableCode", table.Code), ("columnCode", column.Code));
            }

            if (!HasText(column.NameL10n))
            {
                throw Invalid("Підпис стовпця обов'язковий.", "err.ECR-PRM-0422.nameRequired", ("item", column.Code));
            }

            if (!DataTypes.Contains(column.DataType, StringComparer.Ordinal))
            {
                throw Invalid(
                    $"Тип стовпця «{column.DataType}» не підтримується.", "err.ECR-PRM-0422.columnTypeUnknown",
                    ("columnCode", column.Code), ("dataType", column.DataType ?? string.Empty));
            }

            if (column.Group is not null && !EcrCode.TryCreate(column.Group, out _))
            {
                throw Invalid(
                    $"Код групи «{column.Group}» стовпця «{column.Code}» недопустимий.", "err.ECR-PRM-0422.groupCodeInvalid",
                    ("columnCode", column.Code), ("group", column.Group));
            }

            ValidateNumeric(column);
            ValidateList(typeCode, table, column, tableCodes);
        }

        if (!table.Columns.Any(c => c.Key))
        {
            throw Invalid(
                $"У таблиці «{table.Code}» немає ключового стовпця: рядок не мав би адреси.",
                "err.ECR-PRM-0422.tableWithoutKey",
                ("tableCode", table.Code));
        }
    }

    private static void ValidateNumeric(PermitTypeColumnDto column)
    {
        if (column.Precision is null && column.Scale is null)
        {
            return;
        }

        var numeric = string.Equals(column.DataType, nameof(CellDataType.Decimal), StringComparison.Ordinal);
        if (!numeric
            || column.Precision is null
            || column.Precision is < 1 or > Ecr.Domain.Entities.Configuration.PermitFieldMeta.MaxPrecision
            || (column.Scale is { } scale && scale > column.Precision))
        {
            throw Invalid(
                $"Точність і масштаб стовпця «{column.Code}» некоректні: лише Decimal, точність 1..38, масштаб не більший за точність.",
                "err.ECR-PRM-0422.numericFormat",
                ("columnCode", column.Code));
        }
    }

    private static void ValidateList(
        string typeCode, PermitTypeTableDto table, PermitTypeColumnDto column, HashSet<string> tableCodes)
    {
        var lookup = string.Equals(column.DataType, nameof(CellDataType.Lookup), StringComparison.Ordinal);
        var hasListData = column.ListScope is not null
                          || column.ListRegistryCode is not null
                          || column.SiblingTableCode is not null
                          || column.Choices is not null;

        if (!lookup)
        {
            if (hasListData)
            {
                throw Invalid(
                    $"Стовпець «{column.Code}» не посилання, але має налаштування списку.",
                    "err.ECR-PRM-0422.listForNonLookup",
                    ("columnCode", column.Code));
            }

            return;
        }

        if (column.ListScope is null || int.TryParse(column.ListScope, out _)
            || !Enum.TryParse<PermitListScope>(column.ListScope, ignoreCase: false, out var scope)
            || !Enum.IsDefined(scope))
        {
            throw Invalid(
                $"Стовпець «{column.Code}» (Lookup) не має відомої області списку.",
                "err.ECR-PRM-0422.listInconsistent",
                ("columnCode", column.Code));
        }

        switch (scope)
        {
            case PermitListScope.SharedRegistry:
                if (string.IsNullOrWhiteSpace(column.ListRegistryCode) || column.SiblingTableCode is not null || column.Choices is not null)
                {
                    throw Invalid(
                        $"Спільний список стовпця «{column.Code}» потребує лише код довідника.",
                        "err.ECR-PRM-0422.listInconsistent",
                        ("columnCode", column.Code));
                }

                break;

            case PermitListScope.TypeOnly:
                if (column.SiblingTableCode is not null)
                {
                    throw Invalid(
                        $"Список «лише для типу» стовпця «{column.Code}» не має сестринської таблиці.",
                        "err.ECR-PRM-0422.listInconsistent",
                        ("columnCode", column.Code));
                }

                if (column.Choices is null || column.Choices.Count == 0)
                {
                    throw Invalid(
                        $"Список «лише для типу» стовпця «{column.Code}» не має значень.",
                        "err.ECR-PRM-0422.typeOnlyNoChoices",
                        ("columnCode", column.Code));
                }

                if (column.Choices.Any(string.IsNullOrWhiteSpace)
                    || column.Choices.Distinct(StringComparer.OrdinalIgnoreCase).Count() != column.Choices.Count)
                {
                    throw Invalid(
                        $"Значення списку стовпця «{column.Code}» порожні або повторюються.",
                        "err.ECR-PRM-0422.choicesInvalid",
                        ("columnCode", column.Code));
                }

                var listCode = PermitHeaderFields.ListRegistryCode(typeCode, table.Code, column.Code);
                if (!EcrCode.TryCreate(listCode, out _))
                {
                    throw Invalid(
                        $"Код реєстру списку «{listCode}» задовгий.", "err.ECR-PRM-0422.registryCodeInvalid",
                        ("registryCode", listCode));
                }

                break;

            default:
                if (string.IsNullOrWhiteSpace(column.SiblingTableCode) || column.ListRegistryCode is not null || column.Choices is not null)
                {
                    throw Invalid(
                        $"Список «таблиця того самого дозволу» стовпця «{column.Code}» потребує лише код таблиці.",
                        "err.ECR-PRM-0422.listInconsistent",
                        ("columnCode", column.Code));
                }

                if (string.Equals(column.SiblingTableCode, table.Code, StringComparison.OrdinalIgnoreCase)
                    || !tableCodes.Contains(column.SiblingTableCode))
                {
                    throw Invalid(
                        $"Сестринської таблиці «{column.SiblingTableCode}» для стовпця «{column.Code}» немає.",
                        "err.ECR-PRM-0422.siblingUnknown",
                        ("columnCode", column.Code), ("siblingTableCode", column.SiblingTableCode));
                }

                break;
        }
    }

    /// <summary>Таблиця, що посилається на сестринську, яка (через інші) посилається на неї, не публікується.</summary>
    private static void ValidateSiblingCycles(IReadOnlyList<PermitTypeTableDto> tables)
    {
        var edges = tables.ToDictionary(
            t => t.Code,
            t => t.Columns
                .Where(c => c.SiblingTableCode is not null)
                .Select(c => c.SiblingTableCode!)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);

        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var table in tables)
        {
            Visit(table.Code);
        }

        void Visit(string code)
        {
            if (state.TryGetValue(code, out var s))
            {
                if (s == 1)
                {
                    var chain = string.Join(" → ", path.SkipWhile(p => !string.Equals(p, code, StringComparison.OrdinalIgnoreCase)).Append(code));
                    throw Invalid($"Посилання між таблицями замикаються в коло: {chain}.", "err.ECR-PRM-0422.siblingCycle", ("chain", chain));
                }

                return;
            }

            state[code] = 1;
            path.Add(code);
            foreach (var next in edges.GetValueOrDefault(code) ?? [])
            {
                Visit(next);
            }

            path.RemoveAt(path.Count - 1);
            state[code] = 2;
        }
    }

    private static void ValidateRules(IReadOnlyList<PermitTypeRuleDto> rules)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (!EcrCode.TryCreate(rule.Code, out _))
            {
                throw Invalid($"Код перевірки «{rule.Code}» недопустимий.", "err.ECR-PRM-0422.ruleCodeInvalid", ("ruleCode", rule.Code ?? string.Empty));
            }

            if (!codes.Add(rule.Code))
            {
                throw Invalid($"Перевірка «{rule.Code}» повторюється.", "err.ECR-PRM-0422.ruleDuplicate", ("ruleCode", rule.Code));
            }

            if (string.IsNullOrWhiteSpace(rule.Expression))
            {
                throw Invalid($"Вираз перевірки «{rule.Code}» порожній.", "err.ECR-PRM-0422.ruleExpressionRequired", ("ruleCode", rule.Code));
            }

            if (rule.Severity is not ("Warn" or "Block"))
            {
                throw Invalid(
                    $"Рівень «{rule.Severity}» перевірки «{rule.Code}» не існує.", "err.ECR-PRM-0422.ruleSeverityUnknown",
                    ("ruleCode", rule.Code), ("severity", rule.Severity ?? string.Empty));
            }

            if (!HasText(rule.NameL10n))
            {
                throw Invalid("Назва перевірки обов'язкова.", "err.ECR-PRM-0422.nameRequired", ("item", rule.Code));
            }
        }
    }

    /// <summary>Опублікована таблиця не зникає, стовпець не видаляється й не міняє тип, ключ не змінюється.</summary>
    private static void ValidateAgainstPublished(PermitTypeContent content, PermitTypeContent published)
    {
        foreach (var before in published.Tables)
        {
            var after = content.Tables.FirstOrDefault(t => string.Equals(t.Code, before.Code, StringComparison.OrdinalIgnoreCase));
            if (after is null)
            {
                throw Invalid(
                    $"Таблиця «{before.Code}» опублікована й не видаляється.", "err.ECR-PRM-0422.tableRemoved",
                    ("tableCode", before.Code));
            }

            foreach (var column in before.Columns)
            {
                var next = after.Columns.FirstOrDefault(c => string.Equals(c.Code, column.Code, StringComparison.OrdinalIgnoreCase));
                if (next is null)
                {
                    throw Invalid(
                        $"Стовпець «{column.Code}» опублікований і не видаляється: на його значення посилаються рядки.",
                        "err.ECR-PRM-0422.columnRemoved",
                        ("tableCode", before.Code), ("columnCode", column.Code));
                }

                if (!string.Equals(next.DataType, column.DataType, StringComparison.Ordinal))
                {
                    throw Invalid(
                        $"Тип стовпця «{column.Code}» опублікований і не змінюється.", "err.ECR-PRM-0422.columnRetyped",
                        ("tableCode", before.Code), ("columnCode", column.Code));
                }
            }

            var keysBefore = before.Columns.Where(c => c.Key).Select(c => c.Code).Order(StringComparer.OrdinalIgnoreCase);
            var keysAfter = after.Columns.Where(c => c.Key).Select(c => c.Code).Order(StringComparer.OrdinalIgnoreCase);

            if (!keysBefore.SequenceEqual(keysAfter, StringComparer.OrdinalIgnoreCase))
            {
                throw Invalid(
                    $"Ключ таблиці «{before.Code}» опублікований і не змінюється.", "err.ECR-PRM-0422.keyChanged",
                    ("tableCode", before.Code));
            }
        }
    }

    private static bool HasText(IReadOnlyDictionary<string, string>? text)
        => text is not null && text.Values.Any(v => !string.IsNullOrWhiteSpace(v));

    /// <remarks>
    /// ⚠ Ключ — повним літералом у точці кидка: сторож <c>ErrorTitleCatalogTests</c> звіряє з сідом саме літерали.
    /// </remarks>
    private static BusinessRuleException Invalid(string message, string messageKey, params (string Name, string Value)[] details)
    {
        var dictionary = new Dictionary<string, object?> { ["messageKey"] = messageKey };
        foreach (var (name, value) in details)
        {
            dictionary[name] = value;
        }

        return new BusinessRuleException(ErrorCodes.PermitTypeInvalid, message, dictionary);
    }

    /// <summary>Спільного довідника, на який посилається стовпець, немає (перевіряє той, хто має доступ до бази).</summary>
    /// <param name="columnCode">Стовпець.</param>
    /// <param name="registryCode">Код довідника.</param>
    public static BusinessRuleException SharedRegistryUnknown(string columnCode, string registryCode)
        => Invalid(
            $"Спільного довідника «{registryCode}» для стовпця «{columnCode}» не існує.",
            "err.ECR-PRM-0422.sharedRegistryUnknown",
            ("columnCode", columnCode), ("registryCode", registryCode));

    /// <summary>Мінімальна довжина причини публікації.</summary>
    public const int MinReasonLength = 10;

    /// <summary>Причина публікації не коротша за <see cref="MinReasonLength"/> (без пробілів по краях).</summary>
    /// <param name="reason">Причина.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c> <c>reasonTooShort</c>.</exception>
    public static void ValidateReason(string? reason)
    {
        if ((reason?.Trim().Length ?? 0) < MinReasonLength)
        {
            throw Invalid(
                $"Причина публікації має містити щонайменше {MinReasonLength} символів.",
                "err.ECR-PRM-0422.reasonTooShort",
                ("min", MinReasonLength.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
