// src/Ecr.Application/Reporting/ReportRowRules.cs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.Application.Errors;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Reporting;

/// <summary>Правило рядка зрізу: «умова → значення» або «умова → приховати рядок» (<c>D-52a</c>).</summary>
/// <param name="When">Умова — вираз діалекту <c>Report</c> типу <c>Boolean</c> (<c>02b</c> §8a).</param>
/// <param name="Then">Дія: рівно одна з <c>set</c> і <c>hideRow</c>.</param>
public sealed record ReportRuleCommand(string When, ReportRuleThenCommand Then);

/// <summary>Дія правила.</summary>
/// <param name="Set">Присвоїти колонці значення.</param>
/// <param name="HideRow"><c>true</c> — рядок у зріз не потрапляє.</param>
public sealed record ReportRuleThenCommand(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReportRuleSetCommand? Set = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HideRow = null);

/// <summary>Присвоєння колонці рядка.</summary>
/// <param name="Column">Код ОПИСАНОЇ колонки версії.</param>
/// <param name="Value">Вираз діалекту <c>Report</c>; тип сумісний із типом колонки.</param>
public sealed record ReportRuleSetCommand(string Column, string Value);

/// <summary>
/// Правила рядка зрізу (<c>RulesJson</c>, схема 2): перевірка при створенні версії
/// і застосування при побудові — ОДНИМ кодом, щоб вони не розійшлися.
/// </summary>
/// <remarks>
/// ⛔ Правила бачать УСІ поля джерела (<see cref="ReportSourceColumns"/>), а не лише
/// описані колонки: приховати рядок за <c>[UnitCode]</c> можна, не показуючи його.
/// Присвоїти можна лише описаній колонці — інше в зріз не потрапить.
/// </remarks>
public sealed class ReportRowRules
{
    /// <summary>Схема <c>RulesJson</c>, яка несе правила. Схема 1 — без правил, і лишається чинною.</summary>
    public const int Schema = 2;

    /// <summary>Стеля кількості правил версії: кожне обчислюється на КОЖНОМУ рядку зрізу.</summary>
    public const int MaxRules = 100;

    private const string MessageKey = "err.ECR-RPT-0422.rule";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<Compiled> _rules;

    private ReportRowRules(IReadOnlyList<Compiled> rules) => _rules = rules;

    /// <summary>Чи правил немає — тоді побудова йде шляхом схеми 1 без змін.</summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>Чи побудова читає таку схему правил.</summary>
    public static bool IsSupported(int schema) => schema is ReportRules.CurrentSchema or Schema;

    /// <summary>Читає правила збереженої версії; схема 1 — порожньо.</summary>
    /// <param name="rulesJson">Вміст <c>RulesJson</c>.</param>
    /// <param name="describedColumns">Коди колонок опису версії.</param>
    /// <exception cref="BusinessRuleException">Правила не проходять перевірку (заведені повз застосунок).</exception>
    public static ReportRowRules Parse(string? rulesJson, IReadOnlyCollection<string> describedColumns)
    {
        var parsed = ReportRules.Parse(rulesJson);

        if (parsed.Schema != Schema)
        {
            return new ReportRowRules([]);
        }

        // `ReportRules.Parse` щойно прочитав цей самий JSON — він розбирається.
        var stored = JsonSerializer.Deserialize<ReportRulesCommand>(rulesJson!, Options);

        return Compile(parsed.Schema, parsed.RowSource, stored?.Rules, describedColumns);
    }

    /// <summary>Перевіряє правила й готує їх до застосування.</summary>
    /// <param name="schema">Схема правил.</param>
    /// <param name="rowSource">Джерело рядків: воно задає, які колонки бачить вираз.</param>
    /// <param name="rules">Правила в порядку застосування.</param>
    /// <param name="describedColumns">Коди колонок опису версії.</param>
    /// <exception cref="BusinessRuleException">Правило зламане — з його номером (від 1).</exception>
    public static ReportRowRules Compile(
        int schema, string rowSource, IReadOnlyList<ReportRuleCommand>? rules,
        IReadOnlyCollection<string> describedColumns)
    {
        ArgumentNullException.ThrowIfNull(describedColumns);

        rules ??= [];

        if (rules.Count > 0 && schema != Schema)
        {
            // ⛔ Схема 1 правил не читає: прийняти їх означало б мовчки проігнорувати.
            throw Invalid(1, "schema", $"правила несе лише схема {Schema}, а не {schema}");
        }

        if (rules.Count > MaxRules)
        {
            throw Invalid(MaxRules + 1, "count", $"правил більше за {MaxRules}");
        }

        var scope = new ReportExpressionScope(
            ReportSourceColumns.CodesOf(rowSource)
                .Select(code => KeyValuePair.Create(code, TypeOf(ReportSourceColumns.KindOf(rowSource, code)))),
            []);

        var compiled = new List<Compiled>(rules.Count);

        for (var no = 1; no <= rules.Count; no++)
        {
            var rule = rules[no - 1];
            var set = rule?.Then?.Set;
            var hide = rule?.Then?.HideRow == true;

            if (rule is null || (set is null) == !hide)
            {
                throw Invalid(no, "then", "дія — рівно одна з set і hideRow: true");
            }

            var when = Expression(no, "when", rule.When, scope, ExpressionValueType.Boolean);

            if (set is null)
            {
                compiled.Add(new Compiled(when, null, null, default));
                continue;
            }

            if (!describedColumns.Contains(set.Column))
            {
                throw Invalid(no, "column", $"колонки «{set.Column}» в описі версії немає");
            }

            var type = TypeOf(ReportSourceColumns.KindOf(rowSource, set.Column));
            compiled.Add(new Compiled(when, set.Column, Expression(no, "value", set.Value, scope, type), type));
        }

        return new ReportRowRules(compiled);
    }

    /// <summary>Застосовує правила до рядка по порядку; <c>set</c> бачать наступні правила.</summary>
    /// <param name="row">Поля джерела за кодом; присвоєння змінюють словник на місці.</param>
    /// <returns><c>false</c> — рядок приховано.</returns>
    /// <exception cref="BusinessRuleException">Вираз на цьому рядку дав помилку — з номером правила.</exception>
    public bool Apply(Dictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);

        for (var no = 1; no <= _rules.Count; no++)
        {
            var rule = _rules[no - 1];

            // ⛔ `null` (порожня колонка, §6.2) — НЕ «так»: правило не спрацьовує.
            if (Evaluate(no, "when", rule.When, row).Value is not true)
            {
                continue;
            }

            if (rule.Column is null)
            {
                return false;
            }

            var value = Evaluate(no, "value", rule.Value!, row);

            if (!value.IsNull && value.Type != rule.Type)
            {
                throw Invalid(no, "value", $"значення типу {value.Type} не лягає в колонку типу {rule.Type}");
            }

            row[rule.Column] = value.Value;
        }

        return true;
    }

    private static ExpressionValue Evaluate(
        int no, string part, ParsedExpression expression, Dictionary<string, object?> row)
    {
        var value = ReportEvaluation.Evaluate(expression, new ReportRowContext(row, []));

        // ⛔ Гучно: пропущене правило дало б зріз, який виглядає правильним.
        return value.IsError ? throw Invalid(no, part, $"обчислення дало {value.ErrorCode}") : value;
    }

    private static ParsedExpression Expression(
        int no, string part, string? text, ReportExpressionScope scope, ExpressionValueType expected)
    {
        var parsed = new Parser().Parse(text ?? string.Empty, ExpressionDialect.Report);
        var diagnostics = new List<ExpressionDiagnostic>(parsed.Diagnostics);

        if (parsed.Expression is { } expression)
        {
            ReportExpressionChecker.Check(expression, scope, expected, diagnostics);
        }

        return diagnostics.Count == 0 && parsed.Expression is not null
            ? parsed.Expression
            : throw Invalid(no, part, diagnostics.Count == 0 ? "вираз порожній" : diagnostics[0].Message);
    }

    private static ExpressionValueType TypeOf(string? kind)
        => kind switch
        {
            ReportSourceColumns.Number => ExpressionValueType.Number,
            "date" => ExpressionValueType.Date,
            _ => ExpressionValueType.Text,
        };

    private static BusinessRuleException Invalid(int no, string part, string reason)
        => new(
            ErrorCodes.ReportInvalid,
            $"Правило {no} ({part}): {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = MessageKey,
                ["ruleNo"] = no.ToString(CultureInfo.InvariantCulture),
                ["part"] = part,
                ["reason"] = reason,
            });

    private sealed record Compiled(
        ParsedExpression When, string? Column, ParsedExpression? Value, ExpressionValueType Type);
}
