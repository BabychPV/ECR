using System.Globalization;
using System.Text;
using Ecr.Calculations;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Scripting;

namespace Ecr.MethodologyScriptMigration.Conversion;

/// <summary>Де читається константа: це визначає, за якими ключами її резолвить старий рушій.</summary>
internal enum ConstContext : byte
{
    /// <summary>Row-формули й правило категорії: без категорії й без речовини.</summary>
    Row = 0,

    /// <summary>Формули речовини: категорія з правила версії × речовини версії.</summary>
    Substance = 1,

    /// <summary>
    /// Бібліотека: категорію й речовину задає імпортер. Категорія читається з <c>param Category;</c>, речовина — з
    /// <c>CURRENT_SUBSTANCE()</c>.
    /// </summary>
    Library = 2,
}

/// <summary>
/// Плануватор констант (план, розд. 8.3): для кожної прочитаної константи обчислює СПРАВЖНІМ
/// <see cref="ConstantResolver"/> значення для кожної комбінації «категорія × речовина × дата» й спрощує до
/// скаляра, мапи (за категорією / речовиною / обома) або умови за датою.
/// </summary>
internal sealed class ConstantPlanner
{
    private enum ShapeKind : byte { Absent, Scalar, ByCategory, BySubstance, ByCategoryAndSubstance, ByCategoryWithPresence }

    // Presence: лише ByCategoryWithPresence - карта «категорія -> речовини, для яких значення є» (D-556).
    private sealed record Shape(
        ShapeKind Kind, ScriptConst? Value, string? Unit, bool DroppedNullKey = false, ScriptConst? Presence = null);

    private sealed record Piece(DateOnly Start, Shape Shape);

    private sealed class Group(string code, IReadOnlyList<SourceConstant> sources)
    {
        public string Code { get; } = code;
        public IReadOnlyList<SourceConstant> Sources { get; } = sources;
        public List<MethodologyConstant>? Candidates { get; set; }
        public string? Name { get; set; }
        public List<Profile> Profiles { get; } = [];
        public Dictionary<ConstContext, Profile> ByContext { get; } = [];
    }

    private sealed class Profile
    {
        public required string Name { get; init; }
        public required string Signature { get; init; }
        public required string Declaration { get; init; }
        public required Func<string, string> UseExpression { get; init; }
    }

    private static readonly ConstantResolver Resolver = new(null!);

    private readonly VersionSource _version;
    private readonly NameTable _names;
    private readonly List<ConversionIssue> _issues;
    private readonly IReadOnlyList<string> _categoryKeys;
    private readonly bool _hasCategoryRule;
    private readonly string _substanceVar;
    private readonly Dictionary<string, Group> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _substanceIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _unitIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _presenceNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _presenceDeclared = new(StringComparer.Ordinal);
    private readonly List<Profile> _order = [];
    private string? _categoryParam;
    private string? _substanceParam;
    private bool _libraryParamDeclared;

    /// <summary>Ім'я змінної імпортера, яку бібліотека читає через <c>param</c> (мусить збігатися з ім'ям у імпортера).</summary>
    public const string LibraryCategoryParam = "Category";

    /// <summary>Вираз речовини в бібліотеці.</summary>
    public const string LibrarySubstanceExpression = "CURRENT_SUBSTANCE()";

    public ConstantPlanner(
        VersionSource version,
        NameTable names,
        List<ConversionIssue> issues,
        IReadOnlyList<string> categoryKeys,
        bool hasCategoryRule,
        string substanceVar)
    {
        _version = version;
        _names = names;
        _issues = issues;
        _categoryKeys = categoryKeys;
        _hasCategoryRule = hasCategoryRule;
        _substanceVar = substanceVar;

        foreach (var code in version.Substances)
        {
            _substanceIds.TryAdd(code, _substanceIds.Count + 1);
        }

        foreach (var g in version.Constants.GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase))
        {
            _groups[g.Key] = new Group(g.Key, [.. g]);
        }
    }

    /// <summary>
    /// Вираз читання константи у скрипті. Перше звернення планує константу й резервує її ім'я.
    /// </summary>
    /// <param name="code">Код (після <c>CST.</c>).</param>
    /// <param name="context">Де читається.</param>
    /// <param name="categoryVar">Ім'я змінної категорії у скрипті (<c>Category</c> або <c>SubstanceCategory</c>).</param>
    public string Use(string code, ConstContext context, string? categoryVar)
    {
        if (!_groups.TryGetValue(code, out var group))
        {
            group = new Group(code, []);
            _groups[code] = group;
        }

        var profile = ProfileOf(group, context);
        return profile.UseExpression(categoryVar ?? "Category");
    }

    /// <summary>Оголошення прочитаних констант у порядку першого читання.</summary>
    public string RenderDeclarations()
    {
        var sb = new StringBuilder();
        if (_libraryParamDeclared)
        {
            sb.Append("param ").Append(LibraryCategoryParam).Append(";\n");
        }

        foreach (var profile in _order)
        {
            sb.Append(profile.Declaration);
        }

        return sb.ToString();
    }

    /// <summary>Чи прочитано хоч одну константу.</summary>
    public bool HasDeclarations => _order.Count > 0 || _libraryParamDeclared;

    /// <summary>Чи бібліотека читає категорію імпортера (<c>param Category;</c> у тексті).</summary>
    public bool UsesLibraryParam => _libraryParamDeclared;

    private Profile ProfileOf(Group group, ConstContext context)
    {
        if (group.ByContext.TryGetValue(context, out var cached))
        {
            return cached;
        }

        var profile = ProfileOfCore(group, context);
        group.ByContext[context] = profile;
        return profile;
    }

    private Profile ProfileOfCore(Group group, ConstContext context)
    {
        var (pieces, anyAmbiguity, ambiguityExample) = Plan(group, context);

        if (context == ConstContext.Library)
        {
            if (pieces.Any(p => p.Shape.Kind is ShapeKind.ByCategory or ShapeKind.ByCategoryAndSubstance
                    or ShapeKind.ByCategoryWithPresence))
            {
                DeclareLibraryParam(group.Code);
            }

            if (pieces.Any(p => p.Shape.DroppedNullKey))
            {
                _issues.Add(new ConversionIssue(
                    IssueSeverity.Warning, "libraryNullKeyIgnored",
                    $"Library constant «{group.Code}» has a value for 'no category' / 'no substance' that differs from the keyed "
                    + "values; a map cannot hold it, so an importer without a category or substance reads #N/A.",
                    group.Code));
            }
        }

        if (anyAmbiguity)
        {
            _issues.Add(new ConversionIssue(
                IssueSeverity.Warning, "constantAmbiguous",
                $"CST.{group.Code}: the old resolver reports an ambiguous choice for some combinations "
                + $"(those keys are left out of the converted constant and read as #N/A). {ambiguityExample}",
                group.Code));
        }

        if (pieces.All(p => p.Shape.Kind == ShapeKind.Absent))
        {
            _issues.Add(new ConversionIssue(
                IssueSeverity.Warning, "constantUnresolvable",
                $"CST.{group.Code}: the old resolver finds no value in context {context}; the converted script reads it as null.",
                group.Code));
        }

        var signature = Signature(pieces);
        var existing = group.Profiles.FirstOrDefault(p => p.Signature == signature);
        if (existing is not null)
        {
            return existing;
        }

        string name;
        if (group.Name is null)
        {
            name = _names.Allocate(
                ScriptText.IsName(group.Code) ? group.Code : SanitizeName(group.Code), "_c", out var renamed);
            if (renamed || !string.Equals(name, group.Code, StringComparison.Ordinal))
            {
                _issues.Add(new ConversionIssue(
                    IssueSeverity.Info, "nameCollision",
                    $"Constant «{group.Code}» is written as «{name}»: the name is taken by a formula, a keyword or another constant.",
                    group.Code));
            }

            group.Name = name;
        }
        else
        {
            name = _names.Allocate(group.Name + "_" + context.ToString().ToLowerInvariant(), "_c", out _);
        }

        var profile = Build(group, name, pieces, context);
        group.Profiles.Add(profile);
        _order.Add(profile);
        return profile;
    }

    private void DeclareLibraryParam(string constantCode)
    {
        if (_libraryParamDeclared)
        {
            return;
        }

        if (!_names.TryReserve(LibraryCategoryParam))
        {
            throw new ConversionException(
                "libraryParamNameTaken",
                $"Library constant «{constantCode}» varies by category, but the name «{LibraryCategoryParam}» (the importer's "
                + "variable read by 'param') is already used by a formula, a constant or an import of this library.");
        }

        _libraryParamDeclared = true;
    }

    private static string SanitizeName(string code)
    {
        var sb = new StringBuilder();
        foreach (var ch in code)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        var text = sb.ToString();
        return text.Length == 0 || char.IsAsciiDigit(text[0]) ? "c_" + text : text;
    }

    // ---- Обчислення за справжнім резолвером ----

    private (List<Piece> Pieces, bool Ambiguity, string Example) Plan(Group group, ConstContext context)
    {
        var candidates = Candidates(group);

        var (categories, substances) = Dimensions(context);
        var dates = BoundaryDates(group);

        var ambiguity = false;
        var example = string.Empty;
        var segments = new List<(DateOnly Start, ResolvedConstant?[,] Table)>();
        foreach (var date in dates)
        {
            var table = new ResolvedConstant?[categories.Count, substances.Count];
            for (var ci = 0; ci < categories.Count; ci++)
            {
                for (var si = 0; si < substances.Count; si++)
                {
                    var substanceCode = substances[si];
                    long? substanceId = substanceCode is null ? null : _substanceIds.GetValueOrDefault(substanceCode);
                    try
                    {
                        table[ci, si] = Resolver.Resolve(
                            candidates, 0, group.Code, categories[ci], substanceId, date, substanceCode);
                    }
                    catch (DomainException ex)
                    {
                        // «неоднозначно» / нерозібране число / мітка категорії: ключа в мапі не буде (читання дасть #N/A).
                        // Бібліотека без категорії імпортера (null) не вибирає між категоріями — це очікувано, не зауваження.
                        if (context != ConstContext.Library || categories[ci] is not null)
                        {
                            ambiguity = true;
                            example = example.Length == 0 ? ex.Message : example;
                        }

                        table[ci, si] = null;
                    }
                }
            }

            if (segments.Count > 0 && SameTable(segments[^1].Table, table))
            {
                continue;
            }

            segments.Add((date, table));
        }

        var pieces = segments
            .Select(s => new Piece(s.Start, BuildShape(s.Table, categories, substances)))
            .ToList();
        return (pieces, ambiguity, example);
    }

    private (List<string?> Categories, List<string?> Substances) Dimensions(ConstContext context)
    {
        switch (context)
        {
            case ConstContext.Row:
                return ([null], [null]);

            case ConstContext.Substance:
                List<string?> cats = _hasCategoryRule ? [.. _categoryKeys] : [null];
                List<string?> subs = _version.Substances.Count > 0 ? [.. _version.Substances] : [null];
                if (cats.Count == 0)
                {
                    cats = [null];
                }

                return (cats, subs);

            default:
                // Категорію й речовину бібліотеці дає імпортер: ключі — її власні категорії й речовини та ті,
                // що названо в її константах.
                var libKeys = _version.Categories
                    .Concat(_version.Constants.Select(c => c.Category)
                        .Where(c => !string.IsNullOrEmpty(c) && c != MethodologyConstant.CommonCategory).Select(c => c!))
                    .Select(ScriptText.BareCategory)
                    .Distinct(StringComparer.Ordinal);
                var libSubKeys = _version.Substances
                    .Concat(_version.Constants.Select(c => c.SubstanceCode).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!))
                    .Distinct(StringComparer.Ordinal);
                List<string?> libCats = [null, .. libKeys];
                List<string?> libSubs = [null, .. libSubKeys];
                return (libCats, libSubs);
        }
    }

    private List<DateOnly> BoundaryDates(Group group)
    {
        var start = _version.EffectiveFrom;
        var dates = new SortedSet<DateOnly> { start };
        foreach (var source in group.Sources)
        {
            foreach (var d in new[] { source.ValidFrom, source.ValidTo })
            {
                if (d is { } date && date > start && (_version.NextEffectiveFrom is not { } next || date < next))
                {
                    dates.Add(date);
                }
            }
        }

        return [.. dates];
    }

    private List<MethodologyConstant> Candidates(Group group)
    {
        if (group.Candidates is not null)
        {
            return group.Candidates;
        }

        var list = new List<MethodologyConstant>();
        var index = 0;
        foreach (var source in group.Sources)
        {
            index++;
            try
            {
                list.Add(ToEntity(source));
            }
            catch (DomainException ex)
            {
                _issues.Add(new ConversionIssue(
                    IssueSeverity.Error, "constantInvalid",
                    $"CST.{group.Code} (value #{index}) cannot be loaded as a constant: {ex.Message} "
                    + "Dropping it would silently change the numbers, so the version is not converted.", group.Code));
            }
        }

        group.Candidates = list;
        return list;
    }

    private MethodologyConstant ToEntity(SourceConstant source)
    {
        var code = EcrCode.Create("c1");
        var unitId = UnitId(source.UnitCode);
        MethodologyConstant entity;
        if (source.Kind == ConstantKind.Numeric)
        {
            entity = source.Number is { } number
                ? new MethodologyConstant(0, code, number, unitId)
                : MethodologyConstant.FromImport(0, code, source.Text ?? string.Empty, ConstantKind.Numeric, unitId);
        }
        else
        {
            entity = MethodologyConstant.OfText(0, code, source.Text ?? string.Empty, source.Kind);
        }

        long? substanceId = null;
        if (source.SubstanceCode is { } substance)
        {
            if (!_substanceIds.TryGetValue(substance, out var id))
            {
                id = _substanceIds.Count + 1;
                _substanceIds[substance] = id;
            }

            substanceId = id;
        }

        entity.SetScope(source.Category, substanceId);
        entity.SetValidity(source.ValidFrom, source.ValidTo);
        return entity;
    }

    private int UnitId(string? unitCode)
    {
        if (unitCode is null)
        {
            return 0;
        }

        if (!_unitIds.TryGetValue(unitCode, out var id))
        {
            id = _unitIds.Count + 1;
            _unitIds[unitCode] = id;
        }

        return id;
    }

    private string? UnitCode(int? unitId)
        => unitId is { } id && id > 0 ? _unitIds.FirstOrDefault(p => p.Value == id).Key : null;

    private static bool SameTable(ResolvedConstant?[,] a, ResolvedConstant?[,] b)
    {
        for (var i = 0; i < a.GetLength(0); i++)
        {
            for (var j = 0; j < a.GetLength(1); j++)
            {
                if (!Equals(a[i, j], b[i, j]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    // ---- Спрощення ----

    private Shape BuildShape(ResolvedConstant?[,] table, List<string?> categories, List<string?> substances)
    {
        var nc = categories.Count;
        var ns = substances.Count;
        var dropped = false;
        var first = table[0, 0];
        var allSame = true;
        var anyPresent = false;
        string? unit = null;
        for (var i = 0; i < nc; i++)
        {
            for (var j = 0; j < ns; j++)
            {
                allSame &= Equals(table[i, j], first);
                if (table[i, j] is { } cell)
                {
                    anyPresent = true;
                    unit ??= UnitCode(cell.UnitId);
                }
            }
        }

        if (!anyPresent)
        {
            return new Shape(ShapeKind.Absent, null, null);
        }

        if (allSame)
        {
            return new Shape(ShapeKind.Scalar, Scalar(first!), unit);
        }

        var rowsUniform = true;
        for (var i = 0; i < nc && rowsUniform; i++)
        {
            for (var j = 1; j < ns; j++)
            {
                rowsUniform &= Equals(table[i, j], table[i, 0]);
            }
        }

        var colsUniform = true;
        for (var j = 0; j < ns && colsUniform; j++)
        {
            for (var i = 1; i < nc; i++)
            {
                colsUniform &= Equals(table[i, j], table[0, j]);
            }
        }

        if (nc > 1 && rowsUniform)
        {
            var map = new Dictionary<string, ScriptConst>(StringComparer.Ordinal);
            for (var i = 0; i < nc; i++)
            {
                if (table[i, 0] is { } cell)
                {
                    if (categories[i] is null)
                    {
                        dropped |= DiffersFromKeyed(table, i, 0, nc, ns);
                        continue;
                    }

                    map[categories[i]!] = Scalar(cell);
                }
            }

            return new Shape(ShapeKind.ByCategory, ScriptConst.FromMap(map), unit, dropped);
        }

        if (ns > 1 && colsUniform)
        {
            var map = new Dictionary<string, ScriptConst>(StringComparer.Ordinal);
            for (var j = 0; j < ns; j++)
            {
                if (table[0, j] is { } cell)
                {
                    if (substances[j] is null)
                    {
                        dropped |= DiffersFromKeyed(table, 0, j, nc, ns);
                        continue;
                    }

                    map[substances[j]!] = Scalar(cell);
                }
            }

            return new Shape(ShapeKind.BySubstance, ScriptConst.FromMap(map), unit, dropped);
        }

        if (WithPresence(table, categories, substances, unit) is { } compact)
        {
            return compact;
        }

        var outer = new Dictionary<string, ScriptConst>(StringComparer.Ordinal);
        for (var i = 0; i < nc; i++)
        {
            var inner = new Dictionary<string, ScriptConst>(StringComparer.Ordinal);
            for (var j = 0; j < ns; j++)
            {
                if (table[i, j] is { } cell)
                {
                    if (substances[j] is null || categories[i] is null)
                    {
                        dropped |= DiffersFromKeyed(table, i, j, nc, ns);
                        continue;
                    }

                    inner[substances[j]!] = Scalar(cell);
                }
            }

            if (inner.Count > 0)
            {
                outer[categories[i]!] = ScriptConst.FromMap(inner);
            }
        }

        return new Shape(ShapeKind.ByCategoryAndSubstance, ScriptConst.FromMap(outer), unit, dropped);
    }

    /// <summary>
    /// D-556: константа «за категорією × речовиною», у якої в КОЖНІЙ категорії всі наявні клітинки однакові (речовин у
    /// категорії може бути менше, ніж у версії). Стара форма — мапа мап — повторює те саме значення для кожної речовини
    /// категорії: у C11_01 це 13 констант по ~1300 записів і 383 тис. знаків, більше за ліміт компілятора. Нова форма:
    /// мапа «категорія → значення» і СПІЛЬНА для всіх констант з тим самим складом карта наявності «категорія → речовина →
    /// true». Читання те саме за результатом: ключа категорії чи речовини немає - <c>#N/A</c>, як і в мапі мап.
    /// </summary>
    /// <returns><c>null</c> - форма не підходить (є неоднакові клітинки в категорії, порожні ключі чи нічого не стискається).</returns>
    private static Shape? WithPresence(
        ResolvedConstant?[,] table, List<string?> categories, List<string?> substances, string? unit)
    {
        var nc = categories.Count;
        var ns = substances.Count;
        if (nc < 2 || ns < 2 || categories.Any(c => c is null) || substances.Any(x => x is null))
        {
            return null;
        }

        var values = new Dictionary<string, ScriptConst>(StringComparer.Ordinal);
        var presence = new Dictionary<string, ScriptConst>(StringComparer.Ordinal);
        var cells = 0;
        for (var i = 0; i < nc; i++)
        {
            ResolvedConstant? value = null;
            var present = new Dictionary<string, ScriptConst>(StringComparer.Ordinal);
            for (var j = 0; j < ns; j++)
            {
                if (table[i, j] is not { } cell)
                {
                    continue;
                }

                if (value is not null && !Equals(value, cell))
                {
                    return null;
                }

                value = cell;
                present[substances[j]!] = ScriptConst.FromScalar(ExpressionValue.Boolean(true));
            }

            if (value is not null)
            {
                values[categories[i]!] = Scalar(value);
                presence[categories[i]!] = ScriptConst.FromMap(present);
                cells += present.Count;
            }
        }

        // Стискання є лише тоді, коли в категорії кілька речовин з одним значенням.
        return values.Count == 0 || cells <= values.Count
            ? null
            : new Shape(ShapeKind.ByCategoryWithPresence, ScriptConst.FromMap(values), unit, false, ScriptConst.FromMap(presence));
    }

    /// <summary>Чи відрізняється клітинка «без категорії / без речовини» від решти (тоді мапа її втрачає).</summary>
    private static bool DiffersFromKeyed(ResolvedConstant?[,] table, int i, int j, int nc, int ns)
    {
        for (var a = 0; a < nc; a++)
        {
            for (var b = 0; b < ns; b++)
            {
                if ((a != i || b != j) && !Equals(table[a, b], table[i, j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ScriptConst Scalar(ResolvedConstant cell)
        => ScriptConst.FromScalar(cell.Number is { } n ? ExpressionValue.Number(n) : ExpressionValue.Text(cell.Text ?? string.Empty));

    private static string Signature(List<Piece> pieces)
    {
        var sb = new StringBuilder();
        foreach (var piece in pieces)
        {
            sb.Append(piece.Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture)).Append('|')
              .Append(piece.Shape.Kind).Append('|').Append(piece.Shape.Unit).Append('|')
              .Append(piece.Shape.Value is null ? string.Empty : ScriptFormatter.FormatConst(piece.Shape.Value))
              .Append(piece.Shape.Presence is null ? string.Empty : "|" + ScriptFormatter.FormatConst(piece.Shape.Presence))
              .Append('\n');
        }

        return sb.ToString();
    }

    // ---- Друк ----

    private Profile Build(Group group, string name, List<Piece> pieces, ConstContext context)
    {
        var signature = Signature(pieces);
        if (pieces.Count == 1)
        {
            var shape = pieces[0].Shape;
            var declaration = StaticDeclaration(name, shape, forceVarForAbsent: true);
            return new Profile
            {
                Name = name,
                Signature = signature,
                Declaration = declaration,
                UseExpression = cat => StaticUse(name, shape, cat, context),
            };
        }

        // Залежить від дати: окремі константи на кожен проміжок + умова за period.EndKey.
        var sb = new StringBuilder();
        var pieceNames = new string[pieces.Count];
        for (var i = 0; i < pieces.Count; i++)
        {
            if (pieces[i].Shape.Kind == ShapeKind.Absent)
            {
                continue;
            }

            pieceNames[i] = _names.Allocate(
                name + "_from_" + pieces[i].Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture), "_c", out _);
            sb.Append(StaticDeclaration(pieceNames[i], pieces[i].Shape, forceVarForAbsent: false));
        }

        var unit = pieces.Select(p => p.Shape.Unit).FirstOrDefault(u => u is not null);
        var unitText = unit is not null && ScriptText.IsName(unit) ? " : " + unit : string.Empty;
        var allScalar = pieces.All(p => p.Shape.Kind is ShapeKind.Scalar or ShapeKind.Absent);

        string Chain(Func<int, string> pieceExpression)
        {
            var text = new StringBuilder();
            for (var i = pieces.Count - 1; i >= 1; i--)
            {
                text.Append("period.EndKey >= ")
                    .Append(pieces[i].Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
                    .Append(" ? ").Append(pieceExpression(i)).Append(" : ");
            }

            return text.Append(pieceExpression(0)).ToString();
        }

        if (allScalar)
        {
            sb.Append("var ").Append(name).Append(unitText).Append(" = ")
              .Append(Chain(i => pieces[i].Shape.Kind == ShapeKind.Absent ? "null" : pieceNames[i])).Append(";\n");
            return new Profile
            {
                Name = name,
                Signature = signature,
                Declaration = sb.ToString(),
                UseExpression = _ => name,
            };
        }

        // Мапа, що залежить від дати, — функція (var-мапу індексувати не можна).
        var needCat = pieces.Any(p => p.Shape.Kind is ShapeKind.ByCategory or ShapeKind.ByCategoryAndSubstance
            or ShapeKind.ByCategoryWithPresence);
        var needSub = pieces.Any(p => p.Shape.Kind is ShapeKind.BySubstance or ShapeKind.ByCategoryAndSubstance
            or ShapeKind.ByCategoryWithPresence);
        var parameters = new List<string>();
        if (needCat)
        {
            _categoryParam ??= _names.Allocate("k_category", "_p", out _);
            parameters.Add(_categoryParam);
        }

        if (needSub)
        {
            _substanceParam ??= _names.Allocate("k_substance", "_p", out _);
            parameters.Add(_substanceParam);
        }

        string PieceCall(int i)
        {
            var shape = pieces[i].Shape;
            return shape.Kind switch
            {
                ShapeKind.Absent => "null",
                ShapeKind.Scalar => pieceNames[i],
                ShapeKind.ByCategory => pieceNames[i] + "[" + _categoryParam + "]",
                ShapeKind.BySubstance => pieceNames[i] + "[" + _substanceParam + "]",
                ShapeKind.ByCategoryWithPresence => PresenceRead(pieceNames[i], shape, _categoryParam!, _substanceParam!),
                _ => pieceNames[i] + "[" + _categoryParam + "][" + _substanceParam + "]",
            };
        }

        sb.Append("function ").Append(name).Append('(').Append(string.Join(", ", parameters)).Append(") = ")
          .Append(Chain(PieceCall)).Append(";\n");
        return new Profile
        {
            Name = name,
            Signature = signature,
            Declaration = sb.ToString(),
            UseExpression = cat =>
            {
                var args = new List<string>();
                if (needCat)
                {
                    args.Add(CategoryReference(cat, context));
                }

                if (needSub)
                {
                    args.Add(SubstanceReference(context));
                }

                return name + "(" + string.Join(", ", args) + ")";
            },
        };
    }

    private static string CategoryReference(string categoryVar, ConstContext context)
        => context == ConstContext.Library ? LibraryCategoryParam : categoryVar;

    private string SubstanceReference(ConstContext context)
        => context == ConstContext.Library ? LibrarySubstanceExpression : _substanceVar + ".code";

    private string StaticUse(string name, Shape shape, string categoryVar, ConstContext context)
        => shape.Kind switch
        {
            ShapeKind.ByCategoryWithPresence =>
                PresenceRead(name, shape, CategoryReference(categoryVar, context), SubstanceReference(context)),
            ShapeKind.ByCategory => name + "[" + CategoryReference(categoryVar, context) + "]",
            ShapeKind.BySubstance => name + "[" + SubstanceReference(context) + "]",
            ShapeKind.ByCategoryAndSubstance =>
                name + "[" + CategoryReference(categoryVar, context) + "][" + SubstanceReference(context) + "]",
            _ => name,
        };

    private string StaticDeclaration(string name, Shape shape, bool forceVarForAbsent)
    {
        if (shape.Kind == ShapeKind.Absent)
        {
            return forceVarForAbsent ? "var " + name + " = null;\n" : string.Empty;
        }

        var unit = shape.Unit is not null && ScriptText.IsName(shape.Unit) ? " : " + shape.Unit : string.Empty;
        var declaration = "const " + name + unit + " = " + ScriptFormatter.FormatConst(shape.Value!) + ";\n";

        // Карта наявності спільна для констант з тим самим складом: оголошується один раз, перед першим читачем.
        return shape.Presence is null ? declaration : PresenceDeclaration(shape) + declaration;
    }

    /// <summary>Ім'я спільної карти наявності цієї форми.</summary>
    private string PresenceName(Shape shape)
    {
        var key = ScriptFormatter.FormatConst(shape.Presence!);
        if (!_presenceNames.TryGetValue(key, out var name))
        {
            name = _names.Allocate("Present", "_c", out _);
            _presenceNames[key] = name;
        }

        return name;
    }

    private string PresenceDeclaration(Shape shape)
    {
        var name = PresenceName(shape);
        return _presenceDeclared.Add(name)
            ? "const " + name + " = " + ScriptFormatter.FormatConst(shape.Presence!) + ";\n"
            : string.Empty;
    }

    /// <summary>Читання значення форми «категорія → значення» з картою наявності.</summary>
    private string PresenceRead(string valueName, Shape shape, string categoryExpression, string substanceExpression)
        => "(" + PresenceName(shape) + "[" + categoryExpression + "][" + substanceExpression + "] ? "
           + valueName + "[" + categoryExpression + "] : null)";
}
