// src/Ecr.Application/Reporting/ReportSourceColumns.cs
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Reporting;

/// <summary>
/// Таблиця «код колонки опису → поле джерела» (рішення <c>D-52a</c>): що саме
/// опис звіту МОЖЕ попросити в джерела рядків.
/// </summary>
/// <remarks>
/// ⛔ Живе в <c>Application</c>, а не біля будівника: нею перевіряється опис
/// при СТВОРЕННІ версії. Невідомий код, знайдений аж при побудові, означав би
/// опублікований звіт, який відмовляє вночі. Будівник
/// (<c>ReportSnapshotBuilder</c>) тримає до кожного коду спосіб читання; їхню
/// рівність стереже тест.
/// </remarks>
public static class ReportSourceColumns
{
    /// <summary>Текстове значення — лягає у <c>ValueString</c>.</summary>
    public const string Text = "text";

    /// <summary>Числове значення — лягає у <c>ValueNumeric</c>.</summary>
    public const string Number = "number";

    /// <summary>Поля джерела <see cref="ReportDefinitionSpec.CalculationResults"/>.</summary>
    /// <remarks>
    /// Перші п'ять — ті, що будівник писав до <c>D-52a</c> завжди; решта —
    /// поля, які справді є в <c>calc.CalculationResult</c> або досяжні одним
    /// з'єднанням. Дат у цьому джерелі немає: <c>PeriodKey</c> — число.
    /// </remarks>
    private static readonly Dictionary<string, string> CalculationResultKinds = new(StringComparer.Ordinal)
    {
        ["DocumentId"] = Number,
        ["RowKey"] = Text,
        ["OutputCode"] = Text,
        ["Value"] = Number,
        ["SubstanceEntryId"] = Number,
        ["PeriodKey"] = Number,
        ["UnitId"] = Number,
        ["UnitCode"] = Text,
        ["MethodologyVersionId"] = Number,
        ["ProjectCode"] = Text,
    };

    /// <summary>Коди колонок, які джерело вміє віддати.</summary>
    /// <param name="rowSource">Джерело рядків.</param>
    public static IReadOnlyCollection<string> CodesOf(string rowSource)
        => string.Equals(rowSource, ReportDefinitionSpec.CalculationResults, StringComparison.Ordinal)
            ? CalculationResultKinds.Keys
            : [];

    /// <summary>Тип поля джерела; <c>null</c> — такого поля джерело не має.</summary>
    /// <param name="rowSource">Джерело рядків.</param>
    /// <param name="code">Код колонки опису.</param>
    public static string? KindOf(string rowSource, string code)
        => string.Equals(rowSource, ReportDefinitionSpec.CalculationResults, StringComparison.Ordinal)
           && CalculationResultKinds.TryGetValue(code, out var kind)
            ? kind
            : null;

    /// <summary>Відмовляє, якщо колонки опису джерело віддати не може.</summary>
    /// <param name="rowSource">Джерело рядків.</param>
    /// <param name="columns">Колонки опису.</param>
    /// <exception cref="BusinessRuleException">Невідомий код або тип не той, що в джерела.</exception>
    public static void Require(string rowSource, IReadOnlyList<ReportColumnCommand> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        foreach (var column in columns)
        {
            var kind = KindOf(rowSource, column.Code);

            if (kind is null)
            {
                throw new BusinessRuleException(
                    ErrorCodes.ReportInvalid,
                    $"Колонки «{column.Code}» джерело «{rowSource}» не має.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-RPT-0422.unknownColumn",
                        ["columnCode"] = column.Code,
                        ["rowSource"] = rowSource,
                    });
            }

            if (!string.Equals(kind, column.Kind, StringComparison.Ordinal))
            {
                throw new BusinessRuleException(
                    ErrorCodes.ReportInvalid,
                    $"Колонка «{column.Code}» у джерелі має тип «{kind}», а не «{column.Kind}».",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-RPT-0422.columnKindMismatch",
                        ["columnCode"] = column.Code,
                        ["kind"] = column.Kind,
                        ["expectedKind"] = kind,
                    });
            }
        }
    }
}

/// <summary>Прочитані правила версії звіту (<c>RulesJson</c>).</summary>
/// <param name="RowSource">Джерело рядків.</param>
/// <param name="Schema">Версія схеми правил; опис без поля читається як <c>1</c>.</param>
public sealed record ReportRules(string RowSource, int Schema)
{
    /// <summary>Чинна версія схеми <c>RulesJson</c>.</summary>
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Читає правила; порожнє чи зламане — джерело за замовчуванням, схема 1.</summary>
    /// <param name="rulesJson">Вміст <c>RulesJson</c>.</param>
    public static ReportRules Parse(string? rulesJson)
    {
        ReportRulesCommand? stored = null;

        try
        {
            stored = string.IsNullOrWhiteSpace(rulesJson)
                ? null
                : JsonSerializer.Deserialize<ReportRulesCommand>(rulesJson, Options);
        }
        catch (JsonException)
        {
            // Зламані правила = правила за замовчуванням: так їх читали завжди.
        }

        return new ReportRules(
            string.IsNullOrWhiteSpace(stored?.RowSource) ? ReportDefinitionSpec.CalculationResults : stored.RowSource,
            stored?.Schema ?? CurrentSchema);
    }
}
