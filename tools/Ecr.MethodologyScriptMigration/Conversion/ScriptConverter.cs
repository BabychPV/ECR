using System.Globalization;
using System.Text;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Scripting;

namespace Ecr.MethodologyScriptMigration.Conversion;

/// <summary>
/// Перетворює версію методології (формули, константи, правила, категорії, речовини, виходи, імпорти) у текст
/// «ECR Script» (план, розд. 8.2–8.3). Детерміновано, без доступу до БД; чого не вміє — Error і <c>Text = null</c>.
/// </summary>
public static class ScriptConverter
{
    /// <summary>Перетворює версію.</summary>
    /// <param name="source">Версія в пам'яті.</param>
    public static ConversionResult Convert(VersionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Run(source).Execute();
    }

    private sealed class Run(VersionSource vs)
    {
        private readonly List<ConversionIssue> _issues = [];
        private readonly NameTable _names = new();
        private readonly Dictionary<string, string> _formulaNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SourceFormula> _formulas = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SourceOutput> _outputs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _refs = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _optional = new(StringComparer.OrdinalIgnoreCase);
        private ConstantPlanner _planner = null!;

        public ConversionResult Execute()
        {
            var text = Build();
            var hasErrors = _issues.Any(i => i.Severity == IssueSeverity.Error);
            if (hasErrors || text is null)
            {
                return new ConversionResult(null, _issues);
            }

            if (_planner.UsesLibraryParam && !ConverterOptions.LanguageHasParam)
            {
                // ConverterOptions.LanguageHasParam = false: компілятор ще не знає `param`, тож текст не компілюємо.
                Info(
                    "selfCheckSkippedParam",
                    "The script declares 'param'; the compiler does not support it yet (ConverterOptions.LanguageHasParam), "
                    + "so the self-compile check is skipped.");
                return new ConversionResult(text, _issues);
            }

            var errorsBefore = _issues.Count;
            SelfCheck(text);
            return _issues.Skip(errorsBefore).Any(i => i.Severity == IssueSeverity.Error)
                ? new ConversionResult(null, _issues)
                : new ConversionResult(text, _issues);
        }

        private void Error(string code, string message, string? subject = null)
            => _issues.Add(new ConversionIssue(IssueSeverity.Error, code, message, subject));

        private void Info(string code, string message, string? subject = null)
            => _issues.Add(new ConversionIssue(IssueSeverity.Info, code, message, subject));

        private string? Build()
        {
            ValidateStructure();
            ReserveImports();
            if (!vs.IsModuleLevel)
            {
                // Рівень Module віддає аргументи іншим модулем: необов'язковість там — маркер у виразі.
                CollectOptionalArguments();
            }

            var ordered = vs.Formulas.OrderBy(f => f.EvaluationOrder).ToList();
            RegisterFormulas(ordered);

            var hasSubstances = vs.Substances.Count > 0;
            var ruleText = vs.IsLibrary || string.IsNullOrWhiteSpace(vs.CategoryRule) ? null : vs.CategoryRule;
            if (vs.IsLibrary && !string.IsNullOrWhiteSpace(vs.CategoryRule))
            {
                _issues.Add(new ConversionIssue(
                    IssueSeverity.Warning, "libraryCategoryRule", "A library has no category rule; it is ignored."));
            }

            var perSubstanceRule = ruleText is not null && hasSubstances
                                   && ruleText.Contains("SUBSTANCE_APPLICABLE", StringComparison.Ordinal);

            var categoryVar = ruleText is not null ? _names.Allocate("Category", "_k", out _) : "Category";
            var substanceCategoryVar = perSubstanceRule ? _names.Allocate("SubstanceCategory", "_k", out _) : categoryVar;
            var substanceVar = hasSubstances ? _names.Allocate("S", "_s", out _) : "S";

            var knownKeys = KnownCategoryKeys(ruleText is not null);
            var mapKeys = CategoryKeys(ruleText is not null);
            foreach (var key in knownKeys.Where(k => !mapKeys.Contains(k, StringComparer.Ordinal)))
            {
                mapKeys.Add(key);
            }

            _planner = new ConstantPlanner(vs, _names, _issues, mapKeys, ruleText is not null, substanceVar);

            if (vs.IsLibrary)
            {
                return BuildLibrary(ordered);
            }

            if (vs.IsModuleLevel)
            {
                return BuildModule(ordered);
            }

            var rowFormulas = ordered.Where(f => f.Scope == MethodologyFormulaScope.Row).ToList();
            var substanceFormulas = ordered.Where(f => f.Scope != MethodologyFormulaScope.Row).ToList();
            ValidateOutputs(hasSubstances);

            var rowLines = new List<string>();
            foreach (var f in rowFormulas)
            {
                if (Translate(f, ConstContext.Row, null, keyArguments: false) is { } t)
                {
                    rowLines.Add(Statement(f, t, hasSubstances, substanceVar));
                }
            }

            var categoryLines = new List<string>();
            var substanceCategoryLines = new List<string>();
            if (ruleText is not null)
            {
                BuildCategory(
                    ruleText, categoryVar, substanceCategoryVar, knownKeys, perSubstanceRule,
                    categoryLines, substanceCategoryLines, rowFormulas);
            }

            var catForSubstance = perSubstanceRule ? substanceCategoryVar : categoryVar;
            var substanceLines = new List<string>();
            foreach (var f in substanceFormulas)
            {
                if (Translate(f, ConstContext.Substance, catForSubstance, keyArguments: false) is { } t)
                {
                    substanceLines.Add(Statement(f, t, hasSubstances, substanceVar));
                }
            }

            CheckOrder(rowFormulas, substanceFormulas);

            var rowsText = BuildRows();
            if (_issues.Any(i => i.Severity == IssueSeverity.Error))
            {
                return null;
            }

            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendImports(sb);
            sb.Append(rowsText).Append('\n');
            AppendConstants(sb);
            AppendBlock(sb, rowLines);
            AppendBlock(sb, categoryLines);

            if (hasSubstances)
            {
                if (substanceLines.Count > 0)
                {
                    var body = substanceCategoryLines.Concat(substanceLines).ToList();
                    sb.Append("foreach substance ").Append(substanceVar).Append(" in (")
                      .Append(string.Join(", ", vs.Substances.Select(ScriptText.Quote))).Append(") {\n");
                    foreach (var line in body)
                    {
                        foreach (var l in line.TrimEnd('\n').Split('\n'))
                        {
                            sb.Append("    ").Append(l).Append('\n');
                        }
                    }

                    sb.Append("}\n");
                }
            }
            else
            {
                AppendBlock(sb, substanceLines);
            }

            return sb.ToString().TrimEnd('\n') + "\n";
        }

        private string? BuildLibrary(List<SourceFormula> ordered)
        {
            var lines = new List<string>();
            foreach (var f in ordered)
            {
                if (Translate(f, ConstContext.Library, null, keyArguments: false) is { } t)
                {
                    lines.Add("var " + _formulaNames[f.Code] + " = " + t.Text + ";");
                }
            }

            CheckOrder(ordered, []);
            if (vs.RowsClauses.Count > 0)
            {
                _issues.Add(new ConversionIssue(
                    IssueSeverity.Warning, "libraryRowsIgnored", "A library does not select rows; the rows clauses are ignored."));
            }

            if (_issues.Any(i => i.Severity == IssueSeverity.Error))
            {
                return null;
            }

            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendImports(sb);
            AppendConstants(sb);
            AppendBlock(sb, lines);
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        /// <summary>
        /// Рівень <c>Module</c>: формули в порядку обчислення, кожна — <c>output Код : одиниця = вираз;</c> (виходи
        /// модуля — ті, що лягають у рядки <c>F_…</c>; хто куди лягає, знає лише модуль), без <c>rows</c>.
        /// </summary>
        private string? BuildModule(List<SourceFormula> ordered)
        {
            if (vs.Substances.Count > 0)
            {
                Error("moduleSubstances", "A Module-level version cannot have substances.");
            }

            if (ordered.Count == 0)
            {
                Error("moduleNoFormulas", "A Module-level version has no formulas: the script would have no outputs.");
            }

            var lines = new List<string>();
            foreach (var f in ordered)
            {
                if (!_formulaNames.ContainsKey(f.Code))
                {
                    continue;
                }

                var translator = new ExpressionTranslator(
                    code => _planner.Use(code, ConstContext.Row, null), RowFormulaReference, moduleLevel: true);
                try
                {
                    var t = translator.Translate(f.Expression);
                    _refs[f.Code] = [.. t.Formulas];
                    if (t.ParsedInImportMode)
                    {
                        Info("parsedInImportMode", $"Formula «{f.Code}» contains bare argument names (import grammar).", f.Code);
                    }

                    // Одиниця: з виходу, а без неї — з формули; HSE400 одиниць не задає, тож «one» (безрозмірна):
                    // модуль підставляє для неї одиницю колонки місяця, як для формули без одиниці.
                    var unit = _outputs.TryGetValue(f.Code, out var o) && !string.IsNullOrEmpty(o.UnitCode)
                        ? o.UnitCode
                        : f.UnitCode ?? "one";
                    if (!ScriptText.IsName(unit))
                    {
                        Error("invalidUnit", $"Formula «{f.Code}»: unit «{unit}» cannot be written as a script name.", f.Code);
                        continue;
                    }

                    lines.Add("output " + _formulaNames[f.Code] + " : " + unit + " = " + t.Text + ";");
                }
                catch (ConversionException ex)
                {
                    Error(ex.Code, $"Formula «{f.Code}»: {ex.Message}", f.Code);
                }
            }

            CheckOrder(ordered, []);
            if (vs.RowsClauses.Count > 0)
            {
                _issues.Add(new ConversionIssue(
                    IssueSeverity.Warning, "moduleRowsIgnored",
                    "A Module-level script has no rows clause: the module takes every row of the month-column table."));
            }

            if (_issues.Any(i => i.Severity == IssueSeverity.Error))
            {
                return null;
            }

            var sb = new StringBuilder();
            AppendHeader(sb);
            AppendImports(sb);
            AppendConstants(sb);
            AppendBlock(sb, lines);
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        // ---- Перевірки й реєстрація імен ----

        private void ValidateStructure()
        {
            if (vs.IsLibrary)
            {
                if (vs.Outputs.Count > 0)
                {
                    Error("outputInLibrary", "A library methodology has outputs; a library cannot declare output.");
                }
            }
            else if (vs.RowsClauses.Count == 0 && !vs.IsModuleLevel)
            {
                Error("missingRows", "The version has no rows clause: nothing selects the rows to calculate.");
            }

            var dupSubstances = vs.Substances.GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key);
            foreach (var d in dupSubstances)
            {
                Error("duplicateSubstance", $"Substance «{d}» is listed twice.", d);
            }
        }

        private void ReserveImports()
        {
            foreach (var import in vs.Imports)
            {
                if (!ScriptText.IsName(import.LibraryCode))
                {
                    Error("invalidName", $"Library code «{import.LibraryCode}» cannot be written in an import.", import.LibraryCode);
                }
                else if (!_names.TryReserve(import.LibraryCode))
                {
                    Error("duplicateName", $"Library «{import.LibraryCode}» is imported twice.", import.LibraryCode);
                }
            }
        }

        private void RegisterFormulas(List<SourceFormula> ordered)
        {
            foreach (var o in vs.Outputs)
            {
                if (!_outputs.TryAdd(o.Code, o))
                {
                    Error("duplicateOutput", $"Output «{o.Code}» is declared twice (names are compared ignoring case).", o.Code);
                }
            }

            foreach (var f in ordered)
            {
                if (!_formulas.TryAdd(f.Code, f))
                {
                    Error("duplicateFormula", $"Formula «{f.Code}» is declared twice (names are compared ignoring case).", f.Code);
                    continue;
                }

                if (!ScriptText.IsIdentifier(f.Code))
                {
                    Error("invalidName", $"Formula «{f.Code}» is not a valid script name.", f.Code);
                    continue;
                }

                if (_outputs.ContainsKey(f.Code))
                {
                    if (!_names.TryReserve(f.Code))
                    {
                        Error("nameCollision", $"Output «{f.Code}» collides with an import or another name.", f.Code);
                        continue;
                    }

                    _formulaNames[f.Code] = f.Code;
                }
                else
                {
                    var name = _names.Allocate(f.Code, "_f", out var renamed);
                    if (renamed)
                    {
                        Info("nameCollision", $"Formula «{f.Code}» is written as «{name}»: the name is taken.", f.Code);
                    }

                    _formulaNames[f.Code] = name;
                }
            }

            foreach (var o in vs.Outputs)
            {
                if (!_formulas.ContainsKey(o.Code))
                {
                    Error("outputWithoutFormula", $"Output «{o.Code}» has no formula.", o.Code);
                }

                if (!ScriptText.IsName(o.UnitCode))
                {
                    Error("invalidUnit", $"Output «{o.Code}»: unit «{o.UnitCode}» cannot be written as a script name.", o.Code);
                }
            }
        }

        private void ValidateOutputs(bool hasSubstances)
        {
            foreach (var o in vs.Outputs)
            {
                if (!_formulas.TryGetValue(o.Code, out var f))
                {
                    continue;
                }

                var isRow = f.Scope == MethodologyFormulaScope.Row;
                if (hasSubstances && !isRow && !o.IsPerSubstance)
                {
                    Error(
                        "outputScopeMismatch",
                        $"Output «{o.Code}» is not per-substance but its formula runs per substance: the old engine emits such an output from the row phase, where it has no value.",
                        o.Code);
                }
                else if (hasSubstances && isRow && o.IsPerSubstance)
                {
                    Error(
                        "outputScopeMismatch",
                        $"Output «{o.Code}» is per-substance but its formula has Row scope: a script cannot emit one name both as a row var and as a per-substance output.",
                        o.Code);
                }
                else if (!hasSubstances && !isRow && !o.IsPerSubstance)
                {
                    Error(
                        "outputScopeMismatch",
                        $"Output «{o.Code}» is not per-substance but its formula has Substance scope: the old engine emits it from the row phase, where it has no value.",
                        o.Code);
                }
            }
        }

        // ---- Категорії ----

        /// <summary>
        /// «Відомі категорії» старого рушія (<c>GenericCalculationModule.KnownCategoriesOf</c>): категорії КОНСТАНТ версії й
        /// бібліотек, без <c>Common</c>; таблиця <c>MethodologyCategory</c> на розрахунок не впливає (її читає лише публікація).
        /// Порожньо — старий рушій ключ не перевіряє.
        /// </summary>
        private List<string> KnownCategoryKeys(bool hasRule)
        {
            if (!hasRule)
            {
                return [];
            }

            var raw = vs.Constants.Select(c => c.Category)
                .Concat(vs.Imports.SelectMany(i => i.ConstantCategories ?? []))
                .Where(c => !string.IsNullOrWhiteSpace(c) && !string.Equals(c, MethodologyConstant.CommonCategory, StringComparison.Ordinal))
                .Select(c => c!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            var known = new List<string>(raw);
            if (raw.Any(c => c.Any(char.IsWhiteSpace)))
            {
                // Старий IsKnownCategory порівнює ключ і категорії БЕЗ пробілів; скрипт порівнює літерали як є, тому
                // додаємо обидва записи. Інший розподіл пробілів у ключі правила скрипт не прийме — паритет це покаже.
                Warn(
                    "categoryWhitespace",
                    "A constant category contains whitespace: the old engine compares category keys ignoring whitespace; "
                    + "the script accepts the exact and the whitespace-free spelling only.");
                foreach (var c in raw)
                {
                    var bare = string.Concat(c.Where(ch => !char.IsWhiteSpace(ch)));
                    if (!known.Contains(bare, StringComparer.Ordinal))
                    {
                        known.Add(bare);
                    }
                }
            }

            if (vs.Imports.Any(i => i.ConstantCategories is { Count: > 0 } cats
                                    && cats.Any(c => !string.Equals(c, MethodologyConstant.CommonCategory, StringComparison.Ordinal)
                                                     && !vs.Constants.Any(k => string.Equals(k.Category, c, StringComparison.Ordinal)))))
            {
                Info(
                    "libraryCategories",
                    "Categories of imported libraries' constants are added to the accepted category keys (the old engine takes them "
                    + "from the libraries' closure; here all constants of the library version are used).");
            }

            foreach (var c in known.ToList())
            {
                for (var i = 1; i < c.Length; i++)
                {
                    if (c[i] == '_' && !known.Contains(c[..i], StringComparer.Ordinal))
                    {
                        known.Add(c[..i]);
                    }
                }
            }

            return known;
        }

        private void Warn(string code, string message, string? subject = null)
            => _issues.Add(new ConversionIssue(IssueSeverity.Warning, code, message, subject));

        /// <summary>
        /// Необов'язкові аргументи (<c>OptionalArguments.Of</c>): імена з маркером <c>?</c> у <c>ArgumentsCsv</c> КОЖНОЇ формули
        /// версії й бібліотек; властивість версії, а не формули. Маркер у самому виразі рантайм не читає.
        /// Формула без збереженого списку (<c>ArgumentsCsv = null</c>) дає маркери зі свого виразу.
        /// </summary>
        private void CollectOptionalArguments()
        {
            var fromExpressions = new List<(string Formula, string Name)>();
            foreach (var f in vs.Formulas)
            {
                if (f.ArgumentsCsv is not null)
                {
                    _optional.UnionWith(ArgumentDeclarationChecker.OptionalNamesFromCsv(f.ArgumentsCsv));
                    foreach (var a in ArgumentDeclarationChecker.Derive(f.Expression)
                                 .Where(a => a is { Kind: DeclaredArgumentKind.Argument, IsOptional: true }))
                    {
                        fromExpressions.Add((f.Code, ArgumentDeclarationChecker.NormalizeName(a.Name)));
                    }
                }
                else
                {
                    _optional.UnionWith(ArgumentDeclarationChecker.Derive(f.Expression)
                        .Where(a => a is { Kind: DeclaredArgumentKind.Argument, IsOptional: true })
                        .Select(a => ArgumentDeclarationChecker.NormalizeName(a.Name)));
                }
            }

            foreach (var import in vs.Imports.Where(i => i.OptionalArguments is { Count: > 0 }))
            {
                foreach (var name in import.OptionalArguments!)
                {
                    if (_optional.Add(ArgumentDeclarationChecker.NormalizeName(name)))
                    {
                        Info(
                            "libraryOptionalArgument",
                            $"Argument «{name}» is optional through the imported library «{import.LibraryCode}» (union semantics of the old engine; "
                            + "all formulas of the library version are counted, the old engine counts only the needed ones).",
                            import.LibraryCode);
                    }
                }
            }

            foreach (var (formula, name) in fromExpressions.Where(e => !_optional.Contains(e.Name)))
            {
                Warn(
                    "optionalMarkerIgnored",
                    $"Formula «{formula}»: «@{name}?» is marked optional in the expression only; the old engine reads optionality from ArgumentsCsv, "
                    + "so the argument stays required in the script.",
                    formula);
            }
        }

        private bool IsOptionalArgument(string name) => _optional.Contains(ArgumentDeclarationChecker.NormalizeName(name));

        private List<string> AcceptedCategoryKeys()
        {
            var keys = new List<string>();
            foreach (var c in vs.Categories)
            {
                if (!keys.Contains(c, StringComparer.Ordinal))
                {
                    keys.Add(c);
                }
            }

            // Форма B: правило дає {ключ}, константи лежать під {ключ}_{код речовини}; старий IsKnownCategory приймає
            // й префікс перед «_».
            foreach (var c in vs.Categories)
            {
                for (var i = 1; i < c.Length; i++)
                {
                    if (c[i] == '_' && !keys.Contains(c[..i], StringComparer.Ordinal))
                    {
                        keys.Add(c[..i]);
                    }
                }
            }

            return keys;
        }

        private List<string> CategoryKeys(bool hasRule)
        {
            var accepted = AcceptedCategoryKeys();
            if (!hasRule || accepted.Count > 0)
            {
                return accepted;
            }

            var derived = vs.Constants
                .Select(c => c.Category)
                .Where(c => !string.IsNullOrEmpty(c) && c != MethodologyConstant.CommonCategory)
                .Select(c => c!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (derived.Count > 0)
            {
                Info(
                    "categoriesDerived",
                    "The version declares no categories; the category keys are taken from the constants' categories.");
            }

            return derived;
        }

        private void BuildCategory(
            string ruleText,
            string categoryVar,
            string substanceCategoryVar,
            List<string> knownKeys,
            bool perSubstanceRule,
            List<string> categoryLines,
            List<string> substanceCategoryLines,
            List<SourceFormula> rowFormulas)
        {
            var translator = new ExpressionTranslator(
                code => _planner.Use(code, ConstContext.Row, null), RowFormulaReference, keyArguments: true,
                isOptional: IsOptionalArgument);
            TranslatedExpression translated;
            try
            {
                translated = translator.Translate(ruleText);
            }
            catch (ConversionException ex)
            {
                Error(ex.Code, "Category rule: " + ex.Message, "CategoryRule");
                return;
            }

            if (translated.ParsedInImportMode)
            {
                Info("parsedInImportMode", "Category rule contains bare argument names (import grammar).", "CategoryRule");
            }

            var rowCodes = rowFormulas.Select(f => f.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in translated.Formulas.Where(r => _formulas.ContainsKey(r) && !rowCodes.Contains(r)))
            {
                Error(
                    "categoryRuleReadsSubstanceFormula",
                    $"Category rule reads !{reference}, a Substance-scope formula; the old engine evaluates the rule before the substance loop.",
                    "CategoryRule");
            }

            var reject = categoryVar + " == null || " + categoryVar + " == \"\"";
            if (knownKeys.Count > 0)
            {
                reject += WrapInList(categoryVar, knownKeys);
            }

            categoryLines.Add("var " + categoryVar + " = " + translated.Text + ";");
            categoryLines.Add("reject when " + reject + ";");

            if (perSubstanceRule)
            {
                substanceCategoryLines.Add("var " + substanceCategoryVar + " = " + translated.Text + ";");
                substanceCategoryLines.Add(
                    "skip when " + substanceCategoryVar + " == null || " + substanceCategoryVar + " == \"\";");
            }
        }

        private static string WrapInList(string variable, List<string> keys)
        {
            var items = keys.Select(ScriptText.Quote).ToList();
            var single = " || !(" + variable + " in (" + string.Join(", ", items) + "))";
            if (single.Length <= 100)
            {
                return single;
            }

            var sb = new StringBuilder("\n    || !(").Append(variable).Append(" in (\n");
            var line = new StringBuilder("        ");
            for (var i = 0; i < items.Count; i++)
            {
                var piece = items[i] + (i < items.Count - 1 ? ", " : string.Empty);
                if (line.Length + piece.Length > 100 && line.Length > 8)
                {
                    sb.Append(line.ToString().TrimEnd()).Append('\n');
                    line.Clear().Append("        ");
                }

                line.Append(piece);
            }

            return sb.Append(line).Append("\n    ))").ToString();
        }

        // ---- Формули ----

        private TranslatedExpression? Translate(SourceFormula f, ConstContext context, string? categoryVar, bool keyArguments)
        {
            if (!_formulaNames.ContainsKey(f.Code))
            {
                return null;
            }

            var translator = new ExpressionTranslator(
                code => _planner.Use(code, context, categoryVar), RowFormulaReference, keyArguments,
                isOptional: IsOptionalArgument);
            try
            {
                var translated = translator.Translate(f.Expression);
                if (translated.ParsedInImportMode)
                {
                    Info("parsedInImportMode", $"Formula «{f.Code}» contains bare argument names (import grammar).", f.Code);
                }

                _refs[f.Code] = [.. translated.Formulas];
                return translated;
            }
            catch (ConversionException ex)
            {
                Error(ex.Code, $"Formula «{f.Code}»: {ex.Message}", f.Code);
                return null;
            }
        }

        /// <summary><c>!Name</c>: власна формула або член імпортованої бібліотеки.</summary>
        private string RowFormulaReference(string name)
        {
            if (_formulaNames.TryGetValue(name, out var own))
            {
                return own;
            }

            if (_formulas.ContainsKey(name))
            {
                throw new ConversionException("unknownReference", $"!{name} refers to a formula that could not be registered.");
            }

            var hits = new List<string>();
            foreach (var import in vs.Imports)
            {
                var member = import.Members.FirstOrDefault(m => string.Equals(m, name, StringComparison.OrdinalIgnoreCase));
                if (member is not null && ScriptText.IsName(import.LibraryCode) && ScriptText.IsName(member))
                {
                    hits.Add(import.LibraryCode + "." + member);
                }
            }

            return hits.Count switch
            {
                1 => hits[0],
                0 => throw new ConversionException(
                    "unknownReference", $"!{name} is neither a formula of this version nor a member of an imported library."),
                _ => throw new ConversionException(
                    "ambiguousReference", $"!{name} is a member of several imported libraries: {string.Join(", ", hits)}."),
            };
        }

        private string Statement(SourceFormula f, TranslatedExpression t, bool hasSubstances, string substanceVar)
        {
            var name = _formulaNames[f.Code];
            if (_outputs.TryGetValue(f.Code, out var output))
            {
                var per = hasSubstances && f.Scope != MethodologyFormulaScope.Row ? " per " + substanceVar : string.Empty;
                return "output " + name + per + " : " + output.UnitCode + " = " + t.Text + ";";
            }

            if (f.IsVisible && f.UnitCode is not null)
            {
                if (!ScriptText.IsName(f.UnitCode))
                {
                    Error("invalidUnit", $"Visible formula «{f.Code}»: unit «{f.UnitCode}» cannot be written as a script name.", f.Code);
                }

                return "visible var " + name + " : " + f.UnitCode + " = " + t.Text + ";";
            }

            return "var " + name + " = " + t.Text + ";";
        }

        private void CheckOrder(List<SourceFormula> first, List<SourceFormula> second)
        {
            var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in first.Concat(second))
            {
                position.TryAdd(f.Code, position.Count);
            }

            foreach (var f in first.Concat(second))
            {
                if (!_refs.TryGetValue(f.Code, out var refs))
                {
                    continue;
                }

                foreach (var r in refs.Where(r => position.ContainsKey(r) && position[r] >= position[f.Code]))
                {
                    Error(
                        "forwardReference",
                        $"Formula «{f.Code}» reads !{r}, which is evaluated later (or itself): the script can only read names declared above.",
                        f.Code);
                }
            }
        }

        // ---- Склад тексту ----

        private string? BuildRows()
        {
            var sb = new StringBuilder();
            var first = true;
            foreach (var clause in vs.RowsClauses)
            {
                try
                {
                    sb.Append(first ? string.Empty : "\n").Append(RowsClauseWriter.Write(clause));
                    first = false;
                }
                catch (ConversionException ex)
                {
                    Error(ex.Code, ex.Message, string.Join(", ", clause.TableCodes));
                }
            }

            return sb.ToString();
        }

        private void AppendHeader(StringBuilder sb)
        {
            sb.Append("// Methodology ").Append(vs.MethodologyCode).Append(", version ").Append(vs.VersionLabel)
              .Append(vs.IsLibrary ? " (library)" : vs.IsModuleLevel ? " (module level)" : string.Empty).Append('\n');
            sb.Append("// Effective from ").Append(vs.EffectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (vs.NextEffectiveFrom is { } next)
            {
                sb.Append(" until ").Append(next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" (exclusive)");
            }

            sb.Append('\n');
            sb.Append("// Generated by Ecr.MethodologyScriptMigration.\n\n");
        }

        private void AppendImports(StringBuilder sb)
        {
            if (vs.Imports.Count == 0)
            {
                return;
            }

            foreach (var import in vs.Imports)
            {
                sb.Append("import ").Append(import.LibraryCode).Append(";\n");
            }

            sb.Append('\n');
        }

        private void AppendConstants(StringBuilder sb)
        {
            if (_planner.HasDeclarations)
            {
                sb.Append(_planner.RenderDeclarations()).Append('\n');
            }
        }

        private static void AppendBlock(StringBuilder sb, List<string> lines)
        {
            if (lines.Count == 0)
            {
                return;
            }

            foreach (var line in lines)
            {
                sb.Append(line).Append('\n');
            }

            sb.Append('\n');
        }

        // ---- Самоперевірка компілятором ----

        private void SelfCheck(string text)
        {
            var stubs = new Dictionary<string, CompiledScript?>(StringComparer.Ordinal);
            foreach (var import in vs.Imports)
            {
                var members = import.Members.Where(ScriptText.IsName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var stubText = string.Join("\n", members.Select(m => "var " + m + " = 0;")) + "\n";
                var stub = ScriptCompiler.Compile(stubText, new ScriptCompileOptions(vs.NumericMode, true));
                stubs[import.LibraryCode] = stub.Script;
            }

            var compiled = ScriptCompiler.Compile(
                text,
                new ScriptCompileOptions(
                    vs.NumericMode, vs.IsLibrary, code => stubs.GetValueOrDefault(code), IsModuleLevel: vs.IsModuleLevel));
            foreach (var d in compiled.Diagnostics)
            {
                Error(
                    "scriptCompile",
                    $"The generated text does not compile: {d.Code} at {d.Line}:{d.Column}: {d.Message}",
                    d.Code);
            }
        }
    }
}
