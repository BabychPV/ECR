// src/Ecr.Application/Calculations/MethodologyRegistryChecks.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Calculations;

/// <summary>
/// Функції довідників у формулах версії методології при публікації (RT-23b,
/// FEATURE-REGISTRY-TABLES §5.5 перевірки 15–19, §5.8–5.9): довідник, поле, ключ,
/// <c>EntryRef</c> — і ребра <c>cfg.RegistryUse</c>, які публікація переписує.
/// </summary>
/// <remarks>
/// ⛔ Чому при публікації: описка в коді поля (<c>ROW.COMPONENT.WM</c>) у рантаймі дає не
/// аварію, а <c>#REF</c> на кожному рядку нічного прогону — неправильну клітинку звіту,
/// яку помітять на звірці. Тут вона коштує червоного рядка з позицією в редакторі.
///
/// ⚠ Перевіряє та сама пара <see cref="TypeChecker"/>/<see cref="ReferenceResolver"/>, що
/// й шаблон і правила довідника (§5.9), — а не власний обхід: інакше методологія й шаблон
/// розійшлися б у тому, що вважати описаним полем.
///
/// ⚠ З діагностик <see cref="TypeChecker"/> беруться лише ключі функцій довідників
/// (<see cref="BlockingKeys"/>). Загальні типи методології
/// (<c>expr.type.*</c>) судить <see cref="MethodologyPublishChecks"/> за константами версії:
/// тип аргументу <c>@x</c> тут невідомий (він у колонці прив'язаної таблиці, і таблиць
/// може бути кілька), і повний <see cref="TypeChecker"/> над методологією дав би відмови,
/// яких не було до кроку.
/// </remarks>
public static class MethodologyRegistryChecks
{
    /// <summary>
    /// Ключ попередження 21а: фільтр агрегата без індексного шляху — публікацію не зупиняє.
    /// </summary>
    public const string ScanUnindexedKey = "expr.registryScanUnindexed";

    /// <summary>Чи вживає бодай одна формула версії функції довідників.</summary>
    /// <param name="formulas">Розібрані формули версії.</param>
    /// <returns><c>true</c> — є що перевіряти й що писати в <c>cfg.RegistryUse</c>.</returns>
    public static bool UsesRegistries(IReadOnlyList<ParsedFormula> formulas)
    {
        ArgumentNullException.ThrowIfNull(formulas);

        return formulas.Any(f => f.Root is not null && Nodes(f.Root).Any(IsRegistryNode));
    }

    /// <summary>
    /// Форми довідників з БД: усі активні описи з полями й активні ключі тих, що
    /// названі у формулах літералом (<c>REGFIND</c> звіряє частини з первинним ключем).
    /// </summary>
    /// <param name="formulas">Розібрані формули версії.</param>
    /// <param name="registries">Сховище описів довідників.</param>
    /// <param name="keys">Сховище ключів довідників.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Джерело форм і id довідників за кодом — для ребер <c>cfg.RegistryUse</c>.</returns>
    /// <remarks>
    /// ⚠ Описи — усі, а не лише названі: шлях <c>ROW.COMPONENT.MW</c> переходить у ціль
    /// <c>Lookup</c>, якої серед літералів формули немає. Описів десятки
    /// (<see cref="IRegistryStore.ListDefinitionsAsync"/>), тож це один запит.
    /// </remarks>
    public static async Task<RegistryShapeCatalog> LoadShapesAsync(
        IReadOnlyList<ParsedFormula> formulas,
        IRegistryStore registries,
        IRegistryKeyStore keys,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(keys);

        var definitions = await registries.ListDefinitionsAsync(ct).ConfigureAwait(false);
        var named = new HashSet<string>(
            formulas.Where(f => f.Root is not null).SelectMany(f => LiteralCodes(f.Root!)),
            StringComparer.OrdinalIgnoreCase);

        var keysById = new Dictionary<int, IReadOnlyList<RegistryKeyDef>>();
        foreach (var definition in definitions.Where(d => named.Contains(d.Code)))
        {
            keysById[definition.Id] = await keys.ListActiveKeysAsync(definition.Id, ct).ConfigureAwait(false);
        }

        return RegistryShapeCatalog.From(definitions, keysById);
    }

    /// <summary>
    /// Довідник запису, який дає формула (<c>CASE = REGFIND('STREAM_CASE', …)</c>), — щоб
    /// <c>!CASE</c> в іншій формулі був <c>EntryRef</c> цього довідника (§5.3).
    /// </summary>
    /// <param name="formulas">Розібрані формули версії.</param>
    /// <param name="argumentRegistries">Код Lookup-колонки → код довідника для аргументів <c>@Col</c> (RC14); <c>null</c> — невідомо.</param>
    /// <returns>Код формули → код довідника; формули, що дають не запис, відсутні.</returns>
    /// <remarks>
    /// ⚠ Лише форми, де відповідь однозначна: корінь — <c>REGFIND</c>/<c>REGONE</c> з
    /// літералом або посилання на іншу таку формулу. Будь-що складніше (<c>if</c> з
    /// записами різних довідників) лишається «невідомо», тобто не перевіряється, а не
    /// вважається помилкою: здогад тут дав би хибну відмову публікації.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> FormulaRegistries(
        IReadOnlyList<ParsedFormula> formulas, IReadOnlyDictionary<string, string>? argumentRegistries = null)
    {
        ArgumentNullException.ThrowIfNull(formulas);

        // ✎ RC14: аргументи `@Col` з Lookup-колонок — у тому ж словнику під ключем `@ім'я` (код формули
        // не може починатися з `@`), тож `REGFIELD(@Lookup, 'X')` перевіряється й пише ребро RegistryUse.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, registry) in argumentRegistries ?? new Dictionary<string, string>())
        {
            result["@" + name] = registry;
        }

        var roots = formulas
            .Where(f => f.Root is not null)
            .GroupBy(f => f.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Root!, StringComparer.OrdinalIgnoreCase);

        // Ланцюжок !A → !B → REGFIND: кожен прохід додає щонайменше одну формулу або
        // зупиняється; довжина ланцюжка не більша за кількість формул.
        bool changed;
        do
        {
            changed = false;
            foreach (var (code, root) in roots)
            {
                if (result.ContainsKey(code))
                {
                    continue;
                }

                var registry = root switch
                {
                    FunctionNode { Arguments.Count: > 0 } call
                        when call.Name is RegistryForms.Find or RegistryForms.One
                        => ReferenceResolver.RegistryCodeLiteral(call.Arguments[0]),
                    SymbolReferenceNode { Kind: SymbolKind.Formula } other
                        => result.GetValueOrDefault(other.Name),
                    SymbolReferenceNode { Kind: SymbolKind.Argument } arg
                        => result.GetValueOrDefault("@" + arg.Name),
                    _ => null,
                };

                if (registry is not null)
                {
                    result[code] = registry;
                    changed = true;
                }
            }
        }
        while (changed);

        return result;
    }

    /// <summary>
    /// Перевірки 15–19 над формулами версії; відмовляє переліком усіх знахідок з позиціями.
    /// </summary>
    /// <param name="formulas">Розібрані формули версії.</param>
    /// <param name="shapes">Форми довідників.</param>
    /// <param name="warnings">Куди складати попередження 21а; <c>null</c> — нікуди.</param>
    /// <param name="argumentRegistries">Код Lookup-колонки → код довідника для аргументів <c>@Col</c> (RC14); <c>null</c> — невідомо.</param>
    /// <exception cref="BusinessRuleException">
    /// Код першої знахідки: <c>ECR-TMPL-4222</c> (15, 16, 19) або <c>ECR-TMPL-0422</c> (17),
    /// обидва — 422. <c>messageKey</c> і підстановки — першої знахідки, <c>formula</c>,
    /// <c>position</c>, <c>length</c> — її місце; <c>diagnostics</c> — усі.
    /// </exception>
    /// <remarks>
    /// ⚠ Перелік, а не перша знахідка — як і решта перевірок публікації: методолог, що
    /// виправляє описки по одній за прогін, робить стільки прогонів, скільки описок.
    /// </remarks>
    public static void RequireValidReferences(
        IReadOnlyList<ParsedFormula> formulas,
        IRegistryShapeSource shapes,
        ICollection<string>? warnings = null,
        IReadOnlyDictionary<string, string>? argumentRegistries = null)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(shapes);

        var context = new TypeContext(shapes, FormulaRegistries(formulas, argumentRegistries));
        var checker = new TypeChecker();
        var found = new List<(string Formula, ExpressionDiagnostic Diagnostic)>();

        // ⚠ Усі формули, а не лише ті, що самі кличуть функцію довідника: `!CASE * 2` —
        // арифметика над записом, який дала інша формула (перевірка 19).
        foreach (var formula in formulas.Where(f => f.Root is not null))
        {
            var diagnostics = new List<ExpressionDiagnostic>();
            var scans = new List<ExpressionDiagnostic>();
            checker.Check(formula.Root!, context, diagnostics, scans);

            found.AddRange(diagnostics.Where(IsRegistryDiagnostic).Select(d => (formula.Code, d)));

            if (warnings is null)
            {
                continue;
            }

            foreach (var scan in scans.Where(s => s.MessageKey == ScanUnindexedKey))
            {
                warnings.Add($"Формула «{formula.Code}» (позиція "
                             + scan.Position.ToString(CultureInfo.InvariantCulture) + "): " + scan.Message);
            }
        }

        if (found.Count == 0)
        {
            return;
        }

        var (first, diagnostic) = found[0];
        var details = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in diagnostic.MessageParams ?? new Dictionary<string, string>())
        {
            details[name] = value;
        }

        details["messageKey"] = diagnostic.MessageKey;
        details["formula"] = first;
        details["position"] = diagnostic.Position.ToString(CultureInfo.InvariantCulture);
        details["length"] = diagnostic.Length.ToString(CultureInfo.InvariantCulture);
        details["count"] = found.Count.ToString(CultureInfo.InvariantCulture);
        details["diagnostics"] = found
            .Select(f => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["formula"] = f.Formula,
                ["code"] = f.Diagnostic.Code,
                ["message"] = f.Diagnostic.Message,
                ["position"] = f.Diagnostic.Position,
                ["length"] = f.Diagnostic.Length,
                ["messageKey"] = f.Diagnostic.MessageKey,
                ["messageParams"] = f.Diagnostic.MessageParams,
            })
            .ToList();

        throw new BusinessRuleException(
            diagnostic.Code,
            "Формули версії посилаються на довідники, поля чи ключі, яких немає або які вжито "
            + "неправильно; у розрахунку кожне таке посилання дало б #REF: "
            + string.Join(" | ", found.Select(f =>
                $"«{f.Formula}» (позиція {f.Diagnostic.Position.ToString(CultureInfo.InvariantCulture)}): "
                + f.Diagnostic.Message)),
            details);
    }

    /// <summary>
    /// Що читає кожна формула версії: пари «довідник, шлях поля» (§5.8) без повторів.
    /// </summary>
    /// <param name="formulas">Розібрані формули версії.</param>
    /// <param name="argumentRegistries">Код Lookup-колонки → код довідника для аргументів <c>@Col</c> (RC14); <c>null</c> — невідомо.</param>
    /// <returns>
    /// Трійки «формула, довідник, шлях»; шлях <c>null</c> — довідник цілком (<c>REGFIND</c>,
    /// <c>REGONE</c>, агрегат), <c>COMPONENT.MW</c> — поле <c>ROW.COMPONENT.MW</c> довідника
    /// області або <c>REGFIELD</c> від запису відомого довідника.
    /// </returns>
    /// <remarks>
    /// ⚠ Та сама форма, що й ребра правил довідника (<c>RegistryRuleCompiler.UsesOf</c>):
    /// «Де використано» читає обидва види однаково.
    /// </remarks>
    public static IReadOnlyList<(string Formula, string Registry, string? FieldPath)> Uses(
        IReadOnlyList<ParsedFormula> formulas, IReadOnlyDictionary<string, string>? argumentRegistries = null)
    {
        ArgumentNullException.ThrowIfNull(formulas);

        var entries = FormulaRegistries(formulas, argumentRegistries);
        var uses = new List<(string Formula, string Registry, string? FieldPath)>();

        foreach (var formula in formulas.Where(f => f.Root is not null))
        {
            var own = new List<(string Registry, string? Path)>();
            void Add(string registry, string? path)
            {
                if (!own.Exists(u => string.Equals(u.Registry, registry, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(u.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    own.Add((registry, path));
                }
            }

            var pending = new Stack<(AstNode Node, string? Row)>();
            pending.Push((formula.Root!, null));
            while (pending.Count > 0)
            {
                var (node, row) = pending.Pop();
                switch (node)
                {
                    case RowFieldNode field when row is not null:
                        Add(row, string.Join('.', field.Path));
                        break;

                    case FunctionNode call when RegistryForms.Handles(call.Name, ExpressionDialect.Methodology):
                        var code = call.Arguments.Count > 0 && call.Name != RegistryForms.Field
                            ? ReferenceResolver.RegistryCodeLiteral(call.Arguments[0])
                            : null;
                        if (code is not null)
                        {
                            Add(code, null);
                        }

                        if (call.Name == RegistryForms.Field
                            && call.Arguments.Count == 2
                            && EntryRegistry(call.Arguments[0], entries) is { } entryRegistry
                            && ReferenceResolver.RegistryCodeLiteral(call.Arguments[1]) is { } path)
                        {
                            Add(entryRegistry, path);
                        }

                        var opensRow = RegistryForms.RowScopeNames.Contains(call.Name);
                        for (var i = call.Arguments.Count - 1; i >= 0; i--)
                        {
                            pending.Push((call.Arguments[i], opensRow && i > 0 ? code : row));
                        }

                        break;

                    default:
                        foreach (var child in Children(node).Reverse())
                        {
                            pending.Push((child, row));
                        }

                        break;
                }
            }

            uses.AddRange(own.Select(u => (formula.Code, u.Registry, u.Path)));
        }

        return uses;
    }

    /// <summary>
    /// Ребра <c>cfg.RegistryUse</c> версії з пар <see cref="Uses"/>: код довідника — у його id.
    /// </summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="formulas">Розібрані формули версії.</param>
    /// <param name="shapes">Каталог форм — id довідників за кодом.</param>
    /// <param name="argumentRegistries">Код Lookup-колонки → код довідника для аргументів <c>@Col</c> (RC14); <c>null</c> — невідомо.</param>
    /// <returns>Ребра; довідник без id (такого немає) ребра не дає — ключа на нього не буде.</returns>
    public static IReadOnlyList<RegistryUse> BuildUses(
        int methodologyVersionId,
        IReadOnlyList<ParsedFormula> formulas,
        RegistryShapeCatalog shapes,
        IReadOnlyDictionary<string, string>? argumentRegistries = null)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(shapes);

        return
        [
            .. Uses(formulas, argumentRegistries)
                .Select(u => (u.Formula, Id: shapes.IdOf(u.Registry), u.FieldPath))
                .Where(u => u.Id is > 0)
                .Select(u => RegistryUse.ForMethodologyFormula(methodologyVersionId, u.Formula, u.Id!.Value, u.FieldPath)),
        ];
    }

    /// <summary>
    /// Ключі перевірок 15–19, які відхиляють публікацію методології (FEATURE-REGISTRY-TABLES §5.5).
    /// </summary>
    /// <remarks>
    /// ⚠ Перелік, а не префікс: новий ключ <see cref="TypeChecker"/> не має мовчки ставати
    /// відмовою публікації методології — його сюди дописують свідомо.
    /// </remarks>
    public static IReadOnlySet<string> BlockingKeys { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "expr.registryUnknown",
        "expr.registryCodeMustBeLiteral",
        "expr.registryFieldUnknown",
        "expr.registryFieldNotLookup",
        "expr.registryKeyArity",
        "expr.registryKeyPartType",
        "expr.entryRefMisuse",
    };

    private static bool IsRegistryDiagnostic(ExpressionDiagnostic diagnostic)
        => diagnostic.MessageKey is { } key && BlockingKeys.Contains(key);

    private static bool IsRegistryNode(AstNode node)
        => node is RowFieldNode
           || (node is FunctionNode call && RegistryForms.Handles(call.Name, ExpressionDialect.Methodology));

    private static string? EntryRegistry(AstNode node, IReadOnlyDictionary<string, string> entries)
        => node switch
        {
            SymbolReferenceNode { Kind: SymbolKind.Formula } formula => entries.GetValueOrDefault(formula.Name),
            SymbolReferenceNode { Kind: SymbolKind.Argument } arg => entries.GetValueOrDefault("@" + arg.Name),
            FunctionNode { Arguments.Count: > 0 } call when call.Name is RegistryForms.Find or RegistryForms.One
                => ReferenceResolver.RegistryCodeLiteral(call.Arguments[0]),
            _ => null,
        };

    private static IEnumerable<string> LiteralCodes(AstNode root)
        => Nodes(root)
            .OfType<FunctionNode>()
            .Where(call => call.Name != RegistryForms.Field
                           && RegistryForms.Handles(call.Name, ExpressionDialect.Methodology)
                           && call.Arguments.Count > 0)
            .Select(call => ReferenceResolver.RegistryCodeLiteral(call.Arguments[0]))
            .OfType<string>();

    private static IEnumerable<AstNode> Nodes(AstNode root)
    {
        var pending = new Stack<AstNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            foreach (var child in Children(node))
            {
                pending.Push(child);
            }
        }
    }

    private static IEnumerable<AstNode> Children(AstNode node)
        => node switch
        {
            UnaryNode unary => [unary.Operand],
            BinaryNode binary => [binary.Left, binary.Right],
            ConditionalNode conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            FunctionNode function => function.Arguments,
            _ => [],
        };

    /// <summary>
    /// Контекст типів методології для перевірок 15–19: форми довідників і записи, які дають
    /// формули. Решта — «невідомо» (див. зауваження класу).
    /// </summary>
    private sealed class TypeContext(IRegistryShapeSource shapes, IReadOnlyDictionary<string, string> entries)
        : ITypeContext
    {
        public IRegistryShapeSource? Registries => shapes;

        public string? GetFormulaRegistry(string code) => entries.GetValueOrDefault(code);

        public string? GetArgumentRegistry(string name) => entries.GetValueOrDefault("@" + name);

        public ExpressionValueType GetReferenceType(CellReferenceNode reference) => ExpressionValueType.Null;

        public ExpressionValueType GetColumnType(int tableDefId, int columnDefId) => ExpressionValueType.Null;

        public ExpressionValueType GetArgumentType(string name) => ExpressionValueType.Null;
    }
}

/// <summary>
/// Форми довідників з описів БД і їхні id — для перевірок публікації й ребер
/// <c>cfg.RegistryUse</c> (FEATURE-REGISTRY-TABLES §5.9: «у публікації — з БД»).
/// </summary>
public sealed class RegistryShapeCatalog : IRegistryShapeSource
{
    private readonly Dictionary<string, RegistryShape> _shapes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);

    private RegistryShapeCatalog()
    {
    }

    /// <summary>Складає каталог з описів довідників і їхніх активних ключів.</summary>
    /// <param name="definitions">Описи з полями.</param>
    /// <param name="keysByRegistryId">Активні ключі за id довідника; відсутній — ключів немає.</param>
    /// <returns>Каталог форм.</returns>
    public static RegistryShapeCatalog From(
        IReadOnlyList<RegistryDef> definitions,
        IReadOnlyDictionary<int, IReadOnlyList<RegistryKeyDef>> keysByRegistryId)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(keysByRegistryId);

        var codesById = definitions.Where(d => d.Id > 0).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First().Code);
        var catalog = new RegistryShapeCatalog();

        foreach (var definition in definitions)
        {
            var fieldCodes = definition.Fields.Where(f => f.Id > 0).ToDictionary(f => f.Id, f => f.Code);
            var keys = keysByRegistryId.GetValueOrDefault(definition.Id) ?? [];

            catalog._shapes[definition.Code] = new RegistryShape(
                definition.Code,
                [.. definition.Fields.OrderBy(f => f.Ordinal).Select(f => new RegistryFieldShape(
                    f.Code,
                    f.DataType,
                    f.UnitId,
                    f.RefRegistryDefId is { } target ? codesById.GetValueOrDefault(target) : null))],
                [.. keys.Where(k => k.IsActive).Select(k => new RegistryKeyShape(
                    k.Code,
                    k.IsPrimary,
                    [.. k.Fields.OrderBy(f => f.Ordinal)
                        .Select(f => fieldCodes.GetValueOrDefault(f.RegistryFieldDefId) ?? string.Empty)]))]);
            catalog._ids[definition.Code] = definition.Id;
        }

        return catalog;
    }

    /// <inheritdoc />
    public RegistryShape? FindRegistry(string registryCode)
    {
        ArgumentNullException.ThrowIfNull(registryCode);
        return _shapes.GetValueOrDefault(registryCode);
    }

    /// <summary>Id довідника за кодом; <c>null</c> — такого немає.</summary>
    /// <param name="registryCode">Код довідника.</param>
    public int? IdOf(string registryCode)
        => registryCode is not null && _ids.TryGetValue(registryCode, out var id) ? id : null;
}
