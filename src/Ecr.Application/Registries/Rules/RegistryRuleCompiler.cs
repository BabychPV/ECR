// src/Ecr.Application/Registries/Rules/RegistryRuleCompiler.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Registries.Rules;

/// <summary>Правило, готове до виконання (<see cref="RegistryRuleCompiler.Compile"/>).</summary>
/// <param name="Rule">Правило.</param>
/// <param name="Condition">
/// Для <c>Expression</c> — предикат; для <c>RequiredWhen</c> і <c>CrossRegistry</c> — умова, за якої
/// правило застосовується. <c>null</c> — вираз не розібрався (<see cref="IsValid"/> = <c>false</c>).
/// </param>
/// <param name="Field">Поле з параметрів (<c>RequiredWhen</c>, <c>CrossRegistry</c>).</param>
/// <param name="TargetRegistry">Цільовий довідник <c>CrossRegistry</c>.</param>
/// <param name="Measure">
/// Агрегат, значення якого йде в <c>value</c> порушення (шаблон «Сума дочірніх» — Σ, яку показує
/// індикатор сітки, §8.4); <c>null</c> — немає.
/// </param>
/// <param name="Reads">Коди довідників, які читають функції довідників правила (і ціль <c>CrossRegistry</c>).</param>
internal sealed record CompiledRegistryRule(
    RegistryRuleDef Rule,
    AstNode? Condition,
    string? Field,
    string? TargetRegistry,
    AstNode? Measure,
    IReadOnlySet<string> Reads)
{
    /// <summary>Чи можна правило виконати.</summary>
    public bool IsValid => Condition is not null
        && (Rule.RuleKind == RegistryRuleKind.Expression || Field is not null)
        && (Rule.RuleKind != RegistryRuleKind.CrossRegistry || TargetRegistry is not null);
}

/// <summary>Діагностика виразу чи параметрів правила для відмови <c>ruleExpressionInvalid</c>.</summary>
/// <param name="MessageKey">Ключ тексту в каталозі.</param>
/// <param name="Params">Параметри тексту.</param>
/// <param name="Position">Позиція в тексті виразу; <c>-1</c> — діагностика параметрів, а не виразу.</param>
/// <param name="Length">Довжина підсвітки.</param>
public sealed record RegistryRuleDiagnosticDto(
    string MessageKey,
    IReadOnlyDictionary<string, string>? Params,
    int Position,
    int Length);

/// <summary>
/// Розбір і перевірка правил довідника (FEATURE-REGISTRY-TABLES §6, «Публікація опису»; Д-4).
/// </summary>
/// <remarks>
/// <para>
/// Дві половини, одна граматика: <see cref="Compile"/> — для виконання (лише розбір, без форм
/// довідників: правило вже збережене), <see cref="PrepareAsync"/> — для збереження опису (розбір,
/// типи й поля за формами довідників, параметри виду, шаблон «Сума дочірніх»).
/// </para>
/// <para>
/// ⛔ Діалект — <c>Template</c> з господарем <see cref="ExpressionHost.RegistryRule"/> (§6): <c>THIS</c>
/// і <c>ROW.</c> верхнього рівня — запис, що перевіряється. Посилань на комірки, аргументів,
/// констант, формул і шапки в правилі немає: правило про довідник, а не про документ.
/// </para>
/// </remarks>
public sealed class RegistryRuleCompiler(Parser parser)
{
    /// <summary>Ключ діагностики: посилання, якого в правилі довідника немає.</summary>
    public const string ReferenceNotAllowedKey = "registries.rules.referenceNotAllowed";

    /// <summary>Ключ діагностики: вираз не є умовою.</summary>
    public const string NotConditionKey = "registries.rules.notCondition";

    /// <summary>Ключ діагностики: параметр правила відсутній або не відповідає довідникам.</summary>
    public const string ParameterInvalidKey = "registries.rules.parameterInvalid";

    /// <summary>Ключ відмови збереження опису: вираз або параметри правила неправильні.</summary>
    public const string ExpressionInvalidKey = "err.ECR-REG-0422.ruleExpressionInvalid";

    /// <summary>Ключ відмови: нове правило <c>UniqueWithin</c> (<c>R-5</c>, <c>D-154</c>).</summary>
    public const string UniqueWithinReplacedKey = "err.ECR-REG-0422.uniqueWithinReplacedByKeys";

    private static readonly HashSet<string> RegistryFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        RegistryForms.Find, RegistryForms.One, RegistryForms.Sum, RegistryForms.Average,
        RegistryForms.Minimum, RegistryForms.Maximum, RegistryForms.Count,
    };

    /// <summary>Розбирає збережене правило для виконання.</summary>
    /// <param name="rule">Правило (активне, не <c>UniqueWithin</c>).</param>
    internal CompiledRegistryRule Compile(RegistryRuleDef rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var parsed = Parse(rule.Expression);
        var root = parsed.IsSuccess ? parsed.Expression!.Root : null;
        var reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root is not null)
        {
            CollectReads(root, reads);
        }

        var field = rule.RuleKind is RegistryRuleKind.RequiredWhen or RegistryRuleKind.CrossRegistry
            ? RegistryRuleTemplates.Text(rule.ParametersJson, RegistryRuleTemplates.FieldParameter)
            : null;
        var target = rule.RuleKind == RegistryRuleKind.CrossRegistry
            ? RegistryRuleTemplates.Text(rule.ParametersJson, RegistryRuleTemplates.RegistryParameter)
            : null;
        if (target is not null)
        {
            reads.Add(target);
        }

        var measure = root is not null
                      && string.Equals(RegistryRuleTemplates.TemplateOf(rule.ParametersJson), RegistryRuleTemplates.ChildSum, StringComparison.Ordinal)
            ? FirstAggregate(root)
            : null;

        return new CompiledRegistryRule(rule, root, field, target, measure, reads);
    }

    /// <summary>
    /// Готує правила опису до збереження: розгортає шаблони, розбирає й перевіряє нові та змінені
    /// правила. Помилка — відмова всього збереження.
    /// </summary>
    /// <param name="definition">Опис довідника після застосування полів (у пам'яті).</param>
    /// <param name="existing">Наявні правила довідника.</param>
    /// <param name="wanted">Правила з запиту.</param>
    /// <param name="graph">Усі довідники з полями (збережена копія <paramref name="definition"/> заміщується).</param>
    /// <param name="keys">Джерело ключів — для арності <c>REGFIND</c> і ключа <c>CrossRegistry</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Правила з розгорнутими виразами шаблонів.</returns>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0422 ruleExpressionInvalid</c> з діагностикою.</exception>
    /// <remarks>
    /// ⚠ Перевіряються правила нові, змінені (вираз чи параметри) і знову ввімкнені. Незмінене
    /// правило пройшло свою перевірку тоді, коли його зберігали, а старе правило з виразом до RT-17a
    /// (посилання на комірки) не має блокувати правку ІНШОГО правила чи поля — його виконання дає
    /// порушення <c>registries.rules.invalid</c>, а не тишу.
    /// </remarks>
    internal async Task<IReadOnlyList<RegistryRuleSaveDto>> PrepareAsync(
        RegistryDef definition,
        IReadOnlyList<RegistryRuleDef> existing,
        IReadOnlyList<RegistryRuleSaveDto> wanted,
        IReadOnlyList<RegistryDef> graph,
        IRegistryKeyStore keys,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(keys);

        var byId = existing.ToDictionary(r => r.Id);
        var registries = new Dictionary<string, RegistryDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var registry in graph)
        {
            registries[registry.Code] = registry;
        }

        registries[definition.Code] = definition;
        var codesById = registries.Values.Where(r => r.Id > 0).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Code);
        var shapes = new RuleShapes(registries, codesById);

        var result = new List<RegistryRuleSaveDto>(wanted.Count);
        foreach (var rule in wanted)
        {
            if (!Enum.TryParse<RegistryRuleKind>(rule.RuleKind, ignoreCase: false, out var kind)
                || kind == RegistryRuleKind.UniqueWithin
                || !NeedsCheck(rule, byId))
            {
                // Невідомий вид і новий UniqueWithin відхиляє ApplyRules (своїми ключами);
                // наявний UniqueWithin не виконується (R-5) — перевіряти в ньому нічого.
                result.Add(rule);
                continue;
            }

            var diagnostics = new List<RegistryRuleDiagnosticDto>();
            var prepared = rule;
            if (kind == RegistryRuleKind.Expression
                && string.Equals(RegistryRuleTemplates.TemplateOf(rule.ParametersJson), RegistryRuleTemplates.ChildSum, StringComparison.Ordinal))
            {
                var expression = ChildSumExpression(definition, rule.ParametersJson, registries, diagnostics);
                if (expression is not null)
                {
                    prepared = rule with { Expression = expression };
                }
            }

            if (diagnostics.Count == 0)
            {
                await CheckAsync(definition, kind, prepared, shapes, keys, diagnostics, ct).ConfigureAwait(false);
            }

            if (diagnostics.Count > 0)
            {
                throw Invalid(rule.Code, diagnostics);
            }

            result.Add(prepared);
        }

        return result;
    }

    /// <summary>Відмова збереження нового правила <c>UniqueWithin</c> (<c>R-5</c>, §4.7).</summary>
    /// <param name="ruleCode">Код правила.</param>
    public static BusinessRuleException UniqueWithinReplaced(string ruleCode)
        => new(
            "ECR-REG-0422",
            $"Нове правило «{ruleCode}» виду UniqueWithin не приймається: унікальність тепер задає ключ довідника (R-5).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = UniqueWithinReplacedKey,
                ["ruleCode"] = ruleCode,
            });

    private static bool NeedsCheck(RegistryRuleSaveDto rule, Dictionary<int, RegistryRuleDef> existing)
    {
        if (rule.Id is not { } id || !existing.TryGetValue(id, out var stored))
        {
            return true;
        }

        return rule.IsActive
               && (!stored.IsActive
                   || !string.Equals(stored.Expression, rule.Expression, StringComparison.Ordinal)
                   || !string.Equals(stored.ParametersJson, rule.ParametersJson, StringComparison.Ordinal));
    }

    private ParseResult Parse(string expression)
        => parser.Parse(expression ?? string.Empty, ExpressionDialect.Template, ExpressionParseMode.Editor, ExpressionHost.RegistryRule);

    private async Task CheckAsync(
        RegistryDef definition,
        RegistryRuleKind kind,
        RegistryRuleSaveDto rule,
        RuleShapes shapes,
        IRegistryKeyStore keys,
        List<RegistryRuleDiagnosticDto> diagnostics,
        CancellationToken ct)
    {
        var parsed = Parse(rule.Expression);
        if (!parsed.IsSuccess)
        {
            diagnostics.AddRange(parsed.Diagnostics.Select(ToDto));
            return;
        }

        var root = parsed.Expression!.Root;
        ForbiddenReferences(root, rule.Expression, diagnostics);

        var reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectReads(root, reads);
        string? field = null;
        string? target = null;

        if (kind is RegistryRuleKind.RequiredWhen or RegistryRuleKind.CrossRegistry)
        {
            field = RegistryRuleTemplates.Text(rule.ParametersJson, RegistryRuleTemplates.FieldParameter);
            if (field is null || !definition.Fields.Any(f => string.Equals(f.Code, field, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(Parameter(RegistryRuleTemplates.FieldParameter, field));
            }
        }

        if (kind == RegistryRuleKind.CrossRegistry)
        {
            target = RegistryRuleTemplates.Text(rule.ParametersJson, RegistryRuleTemplates.RegistryParameter);
            if (target is null || shapes.Find(target) is null)
            {
                diagnostics.Add(Parameter(RegistryRuleTemplates.RegistryParameter, target));
                target = null;
            }
            else
            {
                reads.Add(target);
            }
        }

        // Ключі — лише довідників, які правило читає: REGFIND і CrossRegistry звіряють арність
        // первинного ключа; решта форм ключів не потребує.
        foreach (var code in reads)
        {
            if (shapes.Find(code) is { Id: > 0 } registry)
            {
                shapes.AddKeys(registry, await keys.ListActiveKeysAsync(registry.Id, ct).ConfigureAwait(false));
            }
        }

        if (target is not null && shapes.FindRegistry(target) is { } targetShape)
        {
            var requestedKey = RegistryRuleTemplates.Text(rule.ParametersJson, RegistryRuleTemplates.KeyParameter);
            var primary = targetShape.PrimaryKey;
            if ((primary?.FieldCodes.Count ?? 1) != 1
                || (requestedKey is not null && !string.Equals(requestedKey, primary?.Code, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(Parameter(RegistryRuleTemplates.KeyParameter, requestedKey ?? primary?.Code));
            }
        }

        var typeDiagnostics = new List<ExpressionDiagnostic>();
        var type = new TypeChecker().Check(root, new RuleTypeContext(shapes, definition.Code), typeDiagnostics);
        diagnostics.AddRange(typeDiagnostics.Select(ToDto));

        if (type is not (ExpressionValueType.Boolean or ExpressionValueType.Null))
        {
            diagnostics.Add(new RegistryRuleDiagnosticDto(NotConditionKey, null, 0, rule.Expression.Length));
        }
    }

    /// <summary>Вираз «Суми дочірніх» з параметрів; <c>null</c> — параметри неправильні (діагностика додана).</summary>
    private static string? ChildSumExpression(
        RegistryDef definition,
        string? parametersJson,
        Dictionary<string, RegistryDef> registries,
        List<RegistryRuleDiagnosticDto> diagnostics)
    {
        var childCode = RegistryRuleTemplates.Text(parametersJson, RegistryRuleTemplates.ChildParameter);
        var child = childCode is null ? null : registries.GetValueOrDefault(childCode);

        // Дитина — довідник, чиє поле композиції вказує саме на довідник правила (§4.8): інакше
        // фільтр ROW.<поле> = THIS порівнював би записи різних довідників.
        var composition = child?.Fields.FirstOrDefault(
            f => f.RelationKind == RegistryRelationKind.Composition && f.RefRegistryDefId == definition.Id && definition.Id > 0);
        if (child is null || composition is null)
        {
            diagnostics.Add(Parameter(RegistryRuleTemplates.ChildParameter, childCode));
            return null;
        }

        var fieldCode = RegistryRuleTemplates.Text(parametersJson, RegistryRuleTemplates.FieldParameter);
        var field = fieldCode is null
            ? null
            : child.Fields.FirstOrDefault(f => string.Equals(f.Code, fieldCode, StringComparison.OrdinalIgnoreCase));
        if (field is null || field.DataType is not (CellDataType.Decimal or CellDataType.Int))
        {
            diagnostics.Add(Parameter(RegistryRuleTemplates.FieldParameter, fieldCode));
            return null;
        }

        var target = RegistryRuleTemplates.Number(parametersJson, RegistryRuleTemplates.TargetParameter);
        var tolerance = RegistryRuleTemplates.Number(parametersJson, RegistryRuleTemplates.ToleranceParameter) ?? 0m;
        if (target is null)
        {
            diagnostics.Add(Parameter(RegistryRuleTemplates.TargetParameter, null));
            return null;
        }

        if (tolerance < 0)
        {
            diagnostics.Add(Parameter(RegistryRuleTemplates.ToleranceParameter, tolerance.ToString(CultureInfo.InvariantCulture)));
            return null;
        }

        return RegistryRuleTemplates.ChildSumExpression(child.Code, composition.Code, field.Code, target.Value, tolerance);
    }

    /// <summary>Посилання документа й методології — у правилі довідника їх немає (§6, «Діалект»).</summary>
    private static void ForbiddenReferences(AstNode root, string text, List<RegistryRuleDiagnosticDto> diagnostics)
    {
        var pending = new Stack<AstNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            switch (node)
            {
                case CellReferenceNode or SymbolReferenceNode or PeriodPropertyNode:
                    var construct = AstPrinter.Print(node);
                    diagnostics.Add(new RegistryRuleDiagnosticDto(
                        ReferenceNotAllowedKey,
                        new Dictionary<string, string> { ["construct"] = construct },
                        node.Position,
                        Math.Min(construct.Length, Math.Max(0, text.Length - node.Position))));
                    break;

                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;

                case BinaryNode binary:
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                    break;

                case ConditionalNode conditional:
                    pending.Push(conditional.WhenFalse);
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.Condition);
                    break;

                case FunctionNode function:
                    foreach (var argument in function.Arguments.Reverse())
                    {
                        pending.Push(argument);
                    }

                    break;
            }
        }
    }

    /// <summary>Коди довідників — перші аргументи-літерали функцій довідників.</summary>
    private static void CollectReads(AstNode root, HashSet<string> reads)
    {
        foreach (var function in Functions(root))
        {
            if (RegistryFunctions.Contains(function.Name)
                && function.Arguments.Count > 0
                && function.Arguments[0] is LiteralNode { Type: ExpressionValueType.Text, Value: string code }
                && !string.IsNullOrWhiteSpace(code))
            {
                reads.Add(code);
            }
        }
    }

    /// <summary>Перший агрегат довідника в дереві (зліва направо).</summary>
    private static FunctionNode? FirstAggregate(AstNode root)
        => Functions(root).FirstOrDefault(f => RegistryForms.RowScopeNames.Contains(f.Name)
                                                && !string.Equals(f.Name, RegistryForms.One, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<FunctionNode> Functions(AstNode root)
    {
        var pending = new Stack<AstNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case FunctionNode function:
                    yield return function;
                    foreach (var argument in function.Arguments.Reverse())
                    {
                        pending.Push(argument);
                    }

                    break;

                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;

                case BinaryNode binary:
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                    break;

                case ConditionalNode conditional:
                    pending.Push(conditional.WhenFalse);
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.Condition);
                    break;
            }
        }
    }

    private static RegistryRuleDiagnosticDto ToDto(ExpressionDiagnostic diagnostic)
        => new(
            diagnostic.MessageKey ?? ExpressionInvalidKey,
            diagnostic.MessageParams,
            diagnostic.Position,
            diagnostic.Length);

    private static RegistryRuleDiagnosticDto Parameter(string parameter, string? value)
        => new(
            ParameterInvalidKey,
            new Dictionary<string, string> { ["parameter"] = parameter, ["value"] = value ?? string.Empty },
            -1,
            0);

    private static BusinessRuleException Invalid(string ruleCode, List<RegistryRuleDiagnosticDto> diagnostics)
        => new(
            "ECR-REG-0422",
            $"Правило «{ruleCode}» не збережено: вираз або параметри неправильні ({diagnostics[0].MessageKey}).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = ExpressionInvalidKey,
                ["ruleCode"] = ruleCode,
                ["diagnostics"] = diagnostics,
            });

    /// <summary>Форми довідників для перевірки типів — з описів у пам'яті.</summary>
    private sealed class RuleShapes(Dictionary<string, RegistryDef> registries, Dictionary<int, string> codesById)
        : IRegistryShapeSource
    {
        private readonly Dictionary<int, IReadOnlyList<RegistryKeyShape>> _keys = [];

        public RegistryDef? Find(string code) => registries.GetValueOrDefault(code);

        public void AddKeys(RegistryDef registry, IReadOnlyList<RegistryKeyDef> keys)
        {
            var fieldCodes = registry.Fields.Where(f => f.Id > 0).ToDictionary(f => f.Id, f => f.Code);
            _keys[registry.Id] = [.. keys.Where(k => k.IsActive).Select(k => new RegistryKeyShape(
                k.Code,
                k.IsPrimary,
                [.. k.Fields.OrderBy(f => f.Ordinal).Select(f => fieldCodes.GetValueOrDefault(f.RegistryFieldDefId) ?? string.Empty)]))];
        }

        public RegistryShape? FindRegistry(string registryCode)
        {
            if (!registries.TryGetValue(registryCode, out var registry))
            {
                return null;
            }

            return new RegistryShape(
                registry.Code,
                [.. registry.Fields.OrderBy(f => f.Ordinal).Select(f => new RegistryFieldShape(
                    f.Code,
                    f.DataType,
                    f.UnitId,
                    f.RefRegistryDefId is { } target ? codesById.GetValueOrDefault(target) : null))],
                _keys.GetValueOrDefault(registry.Id) ?? []);
        }
    }

    /// <summary>Контекст типів правила: документа немає, є лише довідники й <c>THIS</c>.</summary>
    private sealed class RuleTypeContext(IRegistryShapeSource shapes, string ruleRegistry) : ITypeContext
    {
        public IRegistryShapeSource? Registries => shapes;

        public string? RuleRegistryCode => ruleRegistry;

        public ExpressionValueType GetReferenceType(CellReferenceNode reference) => ExpressionValueType.Null;

        public ExpressionValueType GetColumnType(int tableDefId, int columnDefId) => ExpressionValueType.Null;

        public ExpressionValueType GetArgumentType(string name) => ExpressionValueType.Null;
    }
}
