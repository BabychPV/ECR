using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Expressions.Ast;
using Ecr.Scripting.Lexing;
using Ecr.Scripting.Syntax;

namespace Ecr.Scripting.Parsing;

/// <summary>Результат розбору.</summary>
/// <param name="Program">Дерево (може бути неповним, якщо були помилки).</param>
/// <param name="Diagnostics">Діагностики лексера й парсера.</param>
/// <param name="Aborted">Розбір перервано (<c>tooDeep</c>/<c>foreachTooLarge</c>): дерево непридатне.</param>
public sealed record ScriptParseResult(ProgramSyntax Program, IReadOnlyList<ScriptDiagnostic> Diagnostics, bool Aborted);

/// <summary>
/// Парсер «ECR Script»: рекурсивний спуск за граматикою §3.3 + §13. Відновлення після помилки — до наступної
/// <c>;</c> або <c>}</c>; кілька помилок за один прохід. Розгортає <c>foreach X in (…)</c> на рівні лексем (§13.3).
/// </summary>
public static partial class ScriptParser
{
    /// <summary>Межа вкладеності виразів і блоків (<c>tooDeep</c>).</summary>
    public const int MaxNesting = 64;

    /// <summary>Межа рівнів вкладення <c>foreach</c> для повторів.</summary>
    public const int MaxForeachDepth = 3;

    /// <summary>Межа сумарної кількості розгорнутих операторів.</summary>
    public const int MaxExpandedStatements = 2000;

    /// <summary>Лексує й розбирає текст.</summary>
    /// <param name="text">Текст скрипта.</param>
    public static ScriptParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lex = ScriptLexer.Tokenize(text);
        return Parse(lex);
    }

    /// <summary>Розбирає результат лексера (його діагностики входять у результат).</summary>
    /// <param name="lex">Результат лексичного аналізу.</param>
    public static ScriptParseResult Parse(ScriptLexResult lex)
    {
        ArgumentNullException.ThrowIfNull(lex);
        return new Impl([.. lex.Tokens], [.. lex.Diagnostics]).Run();
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    private sealed class Recovery : Exception;

    private sealed class Abort : Exception;

    private sealed class Impl(List<ScriptToken> tokens, List<ScriptDiagnostic> diagnostics)
    {
        private int pos;
        private int nesting;
        private int expanded;

        private ScriptToken Cur => tokens[pos];

        private ScriptToken PeekTok(int k) => tokens[Math.Min(pos + k, tokens.Count - 1)];

        private bool Is(ScriptTokenKind kind) => Cur.Kind == kind;

        private bool IsKw(string word) => Cur.Kind == ScriptTokenKind.Keyword && Cur.Text == word;

        private bool AtEnd => Cur.Kind == ScriptTokenKind.EndOfInput;

        private ScriptToken Advance()
        {
            var t = tokens[pos];
            if (t.Kind != ScriptTokenKind.EndOfInput)
            {
                pos++;
            }

            return t;
        }

        private ScriptSpan SpanFrom(ScriptToken first)
        {
            var last = tokens[Math.Max(pos - 1, 0)];
            var length = Math.Max(0, last.Start + last.Length - first.Start);
            return new ScriptSpan(first.Start, length, first.Line, first.Column);
        }

        private static string Describe(ScriptToken t) => t.Kind == ScriptTokenKind.EndOfInput ? "end of text" : t.Text;

        private void ReportAt(ScriptToken t, string code, string message, params (string, string)[] pairs)
        {
            if (t.AfterInvalid)
            {
                return;
            }

            diagnostics.Add(ScriptDiagnostic.At(code, t.Span, message, pairs));
        }

        private void ReportSpan(string code, ScriptSpan span, string message, params (string, string)[] pairs)
            => diagnostics.Add(ScriptDiagnostic.At(code, span, message, pairs));

        private Recovery FailExpected(string what)
        {
            ReportAt(Cur, ScriptDiagnosticCodes.Expected, $"Expected {what}, found '{Describe(Cur)}'.",
                ("expected", what), ("found", Describe(Cur)));
            return new Recovery();
        }

        private Recovery FailUnexpected(string message)
        {
            ReportAt(Cur, ScriptDiagnosticCodes.UnexpectedToken, message, ("token", Describe(Cur)));
            return new Recovery();
        }

        private ScriptToken Expect(ScriptTokenKind kind, string display)
            => Is(kind) ? Advance() : throw FailExpected(display);

        private ScriptToken ExpectKw(string word)
            => IsKw(word) ? Advance() : throw FailExpected("'" + word + "'");

        private ScriptToken ExpectIdent(string what = "a name")
        {
            if (Is(ScriptTokenKind.Identifier))
            {
                return Advance();
            }

            if (Cur.Kind == ScriptTokenKind.Keyword)
            {
                ReportAt(Cur, ScriptDiagnosticCodes.Expected, $"Expected {what}: '{Cur.Text}' is a reserved word.",
                    ("expected", what), ("found", Cur.Text));
                throw new Recovery();
            }

            throw FailExpected(what);
        }

        private void Enter(ScriptToken at)
        {
            if (++nesting > MaxNesting)
            {
                ReportSpan(ScriptDiagnosticCodes.TooDeep, at.Span,
                    $"Nesting is too deep (more than {MaxNesting} levels).", ("max", MaxNesting.ToString(CultureInfo.InvariantCulture)));
                throw new Abort();
            }
        }

        private void Leave() => nesting--;

        public ScriptParseResult Run()
        {
            var imports = new List<ImportSyntax>();
            var items = new List<ItemSyntax>();
            try
            {
                while (IsKw("import"))
                {
                    var start = pos;
                    try
                    {
                        imports.Add(ParseImport());
                    }
                    catch (Recovery)
                    {
                        Sync(start);
                        if (pos == start)
                        {
                            Advance();
                        }
                    }
                }

                while (!AtEnd)
                {
                    var start = pos;
                    var saved = nesting;
                    try
                    {
                        if (Is(ScriptTokenKind.RBrace) || Is(ScriptTokenKind.Semicolon))
                        {
                            ReportAt(Cur, ScriptDiagnosticCodes.UnexpectedToken, $"Unexpected '{Cur.Text}'.", ("token", Cur.Text));
                            Advance();
                            continue;
                        }

                        if (TryExpandForeach())
                        {
                            continue;
                        }

                        items.Add(ParseItem());
                    }
                    catch (Recovery)
                    {
                        nesting = saved;
                        Sync(start);
                        if (pos == start)
                        {
                            Advance();
                        }
                    }
                }
            }
            catch (Abort)
            {
                return new ScriptParseResult(new ProgramSyntax(imports, items), diagnostics, true);
            }

            return new ScriptParseResult(new ProgramSyntax(imports, items), diagnostics, false);
        }

        private static bool IsStatementStart(ScriptToken t)
            => t.Kind == ScriptTokenKind.Keyword
               && t.Text is "var" or "visible" or "output" or "const" or "function" or "rows" or "table" or "import"
                   or "reject" or "skip" or "if" or "foreach";

        private void Sync(int start)
        {
            var depth = 0;
            for (var k = start; k < pos; k++)
            {
                if (tokens[k].Kind == ScriptTokenKind.LBrace)
                {
                    depth++;
                }
                else if (tokens[k].Kind == ScriptTokenKind.RBrace)
                {
                    depth--;
                }
            }

            depth = Math.Max(0, depth);
            if (pos > start && depth == 0 && IsStatementStart(Cur))
            {
                return;
            }

            while (!AtEnd)
            {
                var t = Cur;
                if (t.Kind == ScriptTokenKind.Semicolon && depth == 0)
                {
                    Advance();
                    return;
                }

                if (t.Kind == ScriptTokenKind.LBrace)
                {
                    depth++;
                    Advance();
                }
                else if (t.Kind == ScriptTokenKind.RBrace)
                {
                    if (depth == 0)
                    {
                        return;
                    }

                    depth--;
                    Advance();
                    if (depth == 0)
                    {
                        if (Is(ScriptTokenKind.Semicolon))
                        {
                            Advance();
                        }

                        return;
                    }
                }
                else
                {
                    Advance();
                }
            }
        }

        // ───── foreach для повторів (§13.3) ─────

        private bool TryExpandForeach()
        {
            if (!IsKw("foreach") || PeekTok(1).Kind != ScriptTokenKind.Identifier)
            {
                return false;
            }

            var start = pos;
            var kw = Advance();
            var name = Advance();
            ExpectKw("in");
            Expect(ScriptTokenKind.LParen, "'('");
            var values = new List<ScriptToken>();
            do
            {
                values.Add(ParseForeachLiteral());
            }
            while (Is(ScriptTokenKind.Comma) && Advance().Kind == ScriptTokenKind.Comma);

            Expect(ScriptTokenKind.RParen, "')'");
            var open = pos;
            Expect(ScriptTokenKind.LBrace, "'{'");
            var depth = 1;
            var close = -1;
            for (var k = open + 1; k < tokens.Count; k++)
            {
                var kind = tokens[k].Kind;
                if (kind == ScriptTokenKind.LBrace)
                {
                    depth++;
                }
                else if (kind == ScriptTokenKind.RBrace && --depth == 0)
                {
                    close = k;
                    break;
                }
            }

            if (close < 0)
            {
                pos = tokens.Count - 1;
                throw FailExpected("'}'");
            }

            if (kw.Depth >= MaxForeachDepth)
            {
                ReportAt(kw, ScriptDiagnosticCodes.ForeachTooLarge,
                    $"foreach is nested too deeply (more than {MaxForeachDepth} levels).",
                    ("limit", MaxForeachDepth.ToString(CultureInfo.InvariantCulture)));
                throw new Abort();
            }

            var inner = tokens.GetRange(open + 1, close - open - 1);
            var perCopy = Math.Max(1, inner.Count(t => t.Kind == ScriptTokenKind.Semicolon));
            expanded += values.Count * perCopy;
            if (expanded > MaxExpandedStatements)
            {
                ReportAt(kw, ScriptDiagnosticCodes.ForeachTooLarge,
                    $"foreach expands to more than {MaxExpandedStatements} statements.",
                    ("limit", MaxExpandedStatements.ToString(CultureInfo.InvariantCulture)));
                throw new Abort();
            }

            var result = new List<ScriptToken>(inner.Count * values.Count);
            foreach (var value in values)
            {
                foreach (var t in inner)
                {
                    result.Add(Substitute(t, name.Text, value, kw.Depth + 1));
                }
            }

            tokens.RemoveRange(start, close - start + 1);
            tokens.InsertRange(start, result);
            pos = start;
            return true;
        }

        private ScriptToken ParseForeachLiteral()
        {
            if (Is(ScriptTokenKind.Minus) && PeekTok(1).Kind == ScriptTokenKind.NumberLiteral)
            {
                var minus = Advance();
                var num = Advance();
                return num with { Text = "-" + num.Text, Value = -(decimal)num.Value!, Start = minus.Start, Line = minus.Line, Column = minus.Column };
            }

            if (Is(ScriptTokenKind.NumberLiteral) || Is(ScriptTokenKind.StringLiteral))
            {
                return Advance();
            }

            throw FailExpected("a number or string literal");
        }

        private ScriptToken Substitute(ScriptToken t, string name, ScriptToken value, int depth)
        {
            var marker = "{" + name + "}";
            if (t.Kind == ScriptTokenKind.Identifier)
            {
                if (t.Text == name)
                {
                    return t with { Kind = value.Kind, Text = value.Text, Value = value.Value, Depth = depth };
                }

                if (t.Text.Contains(marker, StringComparison.Ordinal))
                {
                    var piece = value.Kind == ScriptTokenKind.StringLiteral ? (string)value.Value! : value.Text;
                    var text = t.Text.Replace(marker, piece, StringComparison.Ordinal);
                    if (!text.Contains('{', StringComparison.Ordinal))
                    {
                        if (!IdentifierPattern().IsMatch(text))
                        {
                            ReportAt(t, ScriptDiagnosticCodes.ForeachNameNotIdentifier,
                                $"After substitution '{text}' is not a valid identifier.", ("name", text));
                        }
                        else if (ScriptLexer.Keywords.Contains(text))
                        {
                            return t with { Kind = ScriptTokenKind.Keyword, Text = text, Depth = depth };
                        }
                    }

                    return t with { Text = text, Depth = depth };
                }
            }
            else if (t.Kind == ScriptTokenKind.StringLiteral && t.Value is string s && s.Contains(marker, StringComparison.Ordinal))
            {
                var piece = value.Kind == ScriptTokenKind.StringLiteral ? (string)value.Value! : value.Text;
                return t with { Value = s.Replace(marker, piece, StringComparison.Ordinal), Depth = depth };
            }

            return t with { Depth = depth };
        }

        // ───── елементи ─────

        private ImportSyntax ParseImport()
        {
            var first = Advance();
            var name = ExpectIdent("a library code");
            Expect(ScriptTokenKind.Semicolon, "';'");
            return new ImportSyntax(name.Text, SpanFrom(first));
        }

        private ItemSyntax ParseItem()
        {
            if (Cur.Kind == ScriptTokenKind.Keyword)
            {
                switch (Cur.Text)
                {
                    case "import":
                        throw FailUnexpected("'import' must come before all other declarations.");
                    case "rows":
                        return ParseRows();
                    case "table":
                        return ParseTable();
                    case "const":
                        return ParseConst();
                    case "function":
                        return ParseFunction();
                    default:
                        break;
                }
            }

            return ParseStatement();
        }

        private TableRef ParseTableRef()
        {
            var first = ExpectIdent("a table code");
            if (Is(ScriptTokenKind.Dot))
            {
                Advance();
                var second = ExpectIdent("a table code");
                return new TableRef(first.Text, second.Text) { Span = SpanFrom(first) };
            }

            return new TableRef(null, first.Text) { Span = SpanFrom(first) };
        }

        private RowsSyntax ParseRows()
        {
            var first = Advance();
            ExpectKw("from");
            var tables = new List<TableRef> { ParseTableRef() };
            while (Is(ScriptTokenKind.Comma))
            {
                Advance();
                tables.Add(ParseTableRef());
            }

            MatchNode? where = null;
            MatchNode? active = null;
            if (IsKw("where"))
            {
                Advance();
                where = ParseMatchOr();
            }

            if (IsKw("active"))
            {
                Advance();
                ExpectKw("when");
                active = ParseMatchOr();
            }

            Expect(ScriptTokenKind.Semicolon, "';'");
            return new RowsSyntax(tables, where, active, SpanFrom(first));
        }

        private TableSyntax ParseTable()
        {
            var first = Advance();
            var alias = ExpectIdent("a table alias");
            Expect(ScriptTokenKind.Assign, "'='");
            var table = ParseTableRef();
            Expect(ScriptTokenKind.Semicolon, "';'");
            return new TableSyntax(alias.Text, alias.Span, table, SpanFrom(first));
        }

        private UnitRef? ParseOptionalUnit()
        {
            if (!Is(ScriptTokenKind.Colon))
            {
                return null;
            }

            Advance();
            var unit = ExpectIdent("a unit code");
            return new UnitRef(unit.Text, unit.Span);
        }

        private ConstSyntax ParseConst()
        {
            var first = Advance();
            var name = ExpectIdent();
            var unit = ParseOptionalUnit();
            Expect(ScriptTokenKind.Assign, "'='");
            var value = ParseConstValue();
            Expect(ScriptTokenKind.Semicolon, "';'");
            return new ConstSyntax(name.Text, name.Span, unit, value, SpanFrom(first));
        }

        private ConstValueSyntax ParseConstValue()
        {
            var first = Cur;
            switch (first.Kind)
            {
                case ScriptTokenKind.Minus:
                    Advance();
                    var num = Expect(ScriptTokenKind.NumberLiteral, "a number");
                    return new ConstScalarSyntax(-(decimal)num.Value!, true, SpanFrom(first));
                case ScriptTokenKind.NumberLiteral:
                    Advance();
                    return new ConstScalarSyntax((decimal)first.Value!, true, SpanFrom(first));
                case ScriptTokenKind.StringLiteral:
                    Advance();
                    return new ConstScalarSyntax((string)first.Value!, false, SpanFrom(first));
                case ScriptTokenKind.Keyword when first.Text is "true" or "false":
                    Advance();
                    return new ConstScalarSyntax(first.Text == "true", false, SpanFrom(first));
                case ScriptTokenKind.LBrace:
                    return ParseMap();
                default:
                    throw FailExpected("a constant value (number, string, true/false or { … })");
            }
        }

        private ConstMapSyntax ParseMap()
        {
            var open = Advance();
            Enter(open);
            var entries = new List<ConstEntrySyntax>();
            while (!Is(ScriptTokenKind.RBrace))
            {
                var keyTok = Cur;
                string key;
                if (keyTok.Kind == ScriptTokenKind.Identifier)
                {
                    key = keyTok.Text;
                }
                else if (keyTok.Kind == ScriptTokenKind.StringLiteral)
                {
                    key = (string)keyTok.Value!;
                }
                else if (keyTok.Kind == ScriptTokenKind.NumberLiteral)
                {
                    key = ScriptNumbers.KeyText((decimal)keyTok.Value!);
                }
                else
                {
                    throw FailExpected("a map key (name, string or number)");
                }

                Advance();
                Expect(ScriptTokenKind.Colon, "':'");
                entries.Add(new ConstEntrySyntax(key, keyTok.Span, ParseConstValue()));
                if (Is(ScriptTokenKind.Comma))
                {
                    Advance();
                }
                else
                {
                    break;
                }
            }

            Expect(ScriptTokenKind.RBrace, "'}'");
            Leave();
            return new ConstMapSyntax(entries, SpanFrom(open));
        }

        private FunctionSyntax ParseFunction()
        {
            var first = Advance();
            var name = ExpectIdent();
            Expect(ScriptTokenKind.LParen, "'('");
            var ps = new List<ParamSyntax>();
            if (!Is(ScriptTokenKind.RParen))
            {
                do
                {
                    var p = ExpectIdent("a parameter name");
                    ps.Add(new ParamSyntax(p.Text, p.Span));
                }
                while (Is(ScriptTokenKind.Comma) && Advance().Kind == ScriptTokenKind.Comma);
            }

            Expect(ScriptTokenKind.RParen, "')'");
            Expect(ScriptTokenKind.Assign, "'='");
            var body = ParseExpr();
            Expect(ScriptTokenKind.Semicolon, "';'");
            return new FunctionSyntax(name.Text, name.Span, ps, body, SpanFrom(first));
        }

        // ───── оператори ─────

        private StatementSyntax ParseStatement()
        {
            var first = Cur;
            if (first.Kind == ScriptTokenKind.Keyword)
            {
                switch (first.Text)
                {
                    case "var":
                    case "visible":
                        return ParseVar();
                    case "output":
                        return ParseOutput();
                    case "reject":
                    {
                        Advance();
                        ExpectKw("when");
                        var cond = ParseExpr();
                        Expect(ScriptTokenKind.Semicolon, "';'");
                        return new RejectSyntax(cond, SpanFrom(first));
                    }

                    case "skip":
                    {
                        Advance();
                        ExpectKw("when");
                        var cond = ParseExpr();
                        Expect(ScriptTokenKind.Semicolon, "';'");
                        return new SkipSyntax(cond, SpanFrom(first));
                    }

                    case "if":
                        return ParseIf();
                    case "foreach":
                        return ParseForeachSubstance();
                    case "rows" or "const" or "function" or "table" or "import":
                        throw FailUnexpected($"'{first.Text}' is allowed only at the top level.");
                    default:
                        break;
                }
            }

            throw FailUnexpected($"Unexpected '{Describe(first)}': a statement was expected.");
        }

        private VarSyntax ParseVar()
        {
            var first = Cur;
            var visible = false;
            if (IsKw("visible"))
            {
                visible = true;
                Advance();
            }

            ExpectKw("var");
            var name = ExpectIdent();
            var unit = ParseOptionalUnit();
            Expect(ScriptTokenKind.Assign, "'='");
            var init = ParseExpr();
            Expect(ScriptTokenKind.Semicolon, "';'");
            return new VarSyntax(visible, name.Text, name.Span, unit, init, SpanFrom(first));
        }

        private OutputSyntax ParseOutput()
        {
            var first = Advance();
            var code = ExpectIdent("an output code");
            string? per = null;
            var perSpan = default(ScriptSpan);
            if (IsKw("per"))
            {
                Advance();
                var p = ExpectIdent("a substance variable");
                per = p.Text;
                perSpan = p.Span;
            }

            Expect(ScriptTokenKind.Colon, "':' and a unit");
            var unitTok = ExpectIdent("a unit code");
            Expect(ScriptTokenKind.Assign, "'='");
            var init = ParseExpr();
            Expect(ScriptTokenKind.Semicolon, "';'");
            return new OutputSyntax(code.Text, code.Span, per, perSpan, new UnitRef(unitTok.Text, unitTok.Span), init, SpanFrom(first));
        }

        private IfSyntax ParseIf()
        {
            var first = Advance();
            Expect(ScriptTokenKind.LParen, "'('");
            var cond = ParseExpr();
            Expect(ScriptTokenKind.RParen, "')'");
            var then = ParseBlock();
            BlockSyntax? @else = null;
            if (IsKw("else"))
            {
                Advance();
                if (IsKw("if"))
                {
                    var save = nesting;
                    Enter(Cur);
                    var inner = ParseIf();
                    nesting = save;
                    @else = new BlockSyntax([inner], inner.Span);
                }
                else
                {
                    @else = ParseBlock();
                }
            }

            return new IfSyntax(cond, then, @else, SpanFrom(first));
        }

        private ForeachSubstanceSyntax ParseForeachSubstance()
        {
            var first = Advance();
            ExpectKw("substance");
            var name = ExpectIdent("a substance variable");
            ExpectKw("in");
            Expect(ScriptTokenKind.LParen, "'('");
            var codes = new List<SubstanceCodeSyntax>();
            do
            {
                var s = Expect(ScriptTokenKind.StringLiteral, "a substance code string");
                codes.Add(new SubstanceCodeSyntax((string)s.Value!, s.Span));
            }
            while (Is(ScriptTokenKind.Comma) && Advance().Kind == ScriptTokenKind.Comma);

            Expect(ScriptTokenKind.RParen, "')'");
            var body = ParseBlock();
            return new ForeachSubstanceSyntax(name.Text, name.Span, codes, body, SpanFrom(first));
        }

        private BlockSyntax ParseBlock()
        {
            var open = Expect(ScriptTokenKind.LBrace, "'{'");
            Enter(open);
            var list = new List<StatementSyntax>();
            while (!Is(ScriptTokenKind.RBrace) && !AtEnd)
            {
                var start = pos;
                var saved = nesting;
                try
                {
                    if (TryExpandForeach())
                    {
                        continue;
                    }

                    list.Add(ParseStatement());
                }
                catch (Recovery)
                {
                    nesting = saved;
                    Sync(start);
                    if (pos == start)
                    {
                        Advance();
                    }
                }
            }

            Expect(ScriptTokenKind.RBrace, "'}'");
            Leave();
            return new BlockSyntax(list, SpanFrom(open));
        }

        // ───── умови вибору рядків ─────

        private MatchNode ParseMatchOr()
        {
            var first = Cur;
            Enter(first);
            var left = ParseMatchAnd();
            while (Is(ScriptTokenKind.OrOr))
            {
                Advance();
                var right = ParseMatchAnd();
                left = new MatchOr(left, right) { Span = SpanFrom(first) };
            }

            Leave();
            return left;
        }

        private MatchNode ParseMatchAnd()
        {
            var first = Cur;
            var left = ParseMatchNot();
            while (Is(ScriptTokenKind.AndAnd))
            {
                Advance();
                var right = ParseMatchNot();
                left = new MatchAnd(left, right) { Span = SpanFrom(first) };
            }

            return left;
        }

        private MatchNode ParseMatchNot()
        {
            var first = Cur;
            if (Is(ScriptTokenKind.Bang))
            {
                Advance();
                Enter(first);
                var operand = ParseMatchNot();
                Leave();
                return new MatchNot(operand) { Span = SpanFrom(first) };
            }

            if (Is(ScriptTokenKind.LParen))
            {
                Advance();
                var inner = ParseMatchOr();
                Expect(ScriptTokenKind.RParen, "')'");
                return inner;
            }

            return ParseMatchAtom();
        }

        private MatchNode ParseMatchAtom()
        {
            var first = Cur;
            if (!IsKw("row"))
            {
                throw FailExpected("a condition 'row.Column == value' (or 'in (…)')");
            }

            Advance();
            Expect(ScriptTokenKind.Dot, "'.'");
            var column = ExpectIdent("a column code").Text;
            if (IsKw("in"))
            {
                Advance();
                Expect(ScriptTokenKind.LParen, "'('");
                var values = new List<MatchValue> { ParseMatchValue() };
                while (Is(ScriptTokenKind.Comma))
                {
                    Advance();
                    values.Add(ParseMatchValue());
                }

                Expect(ScriptTokenKind.RParen, "')'");
                return new MatchIn(column, values) { Span = SpanFrom(first) };
            }

            bool negated;
            if (Is(ScriptTokenKind.EqualEqual))
            {
                negated = false;
            }
            else if (Is(ScriptTokenKind.NotEqual))
            {
                negated = true;
            }
            else if (Is(ScriptTokenKind.Assign))
            {
                ReportAt(Cur, ScriptDiagnosticCodes.AssignInExpression, "Single '=' is an assignment: comparison is '=='.");
                negated = false;
            }
            else
            {
                throw FailExpected("'==', '!=' or 'in'");
            }

            Advance();
            var value = ParseMatchValue();
            return new MatchEquals(column, value, negated) { Span = SpanFrom(first) };
        }

        private MatchValue ParseMatchValue()
        {
            if (Is(ScriptTokenKind.StringLiteral))
            {
                return new MatchValue.Text((string)Advance().Value!);
            }

            if (IsKw("null"))
            {
                Advance();
                return new MatchValue.Null();
            }

            if (IsKw("entry"))
            {
                var (registry, code) = ParseEntryArgs();
                return new MatchValue.Entry(registry, code);
            }

            throw FailExpected("a string, entry(\"REGISTRY\", \"CODE\") or null");
        }

        private (string Registry, string Code) ParseEntryArgs()
        {
            Advance();
            Expect(ScriptTokenKind.LParen, "'('");
            var reg = Expect(ScriptTokenKind.StringLiteral, "a registry code string");
            Expect(ScriptTokenKind.Comma, "','");
            var code = Expect(ScriptTokenKind.StringLiteral, "an entry code string");
            Expect(ScriptTokenKind.RParen, "')'");
            return ((string)reg.Value!, (string)code.Value!);
        }

        // ───── вирази ─────

        private Expr ParseExpr()
        {
            var first = Cur;
            Enter(first);
            var cond = ParseOr();
            if (Is(ScriptTokenKind.Question))
            {
                Advance();
                var a = ParseExpr();
                Expect(ScriptTokenKind.Colon, "':'");
                var b = ParseExpr();
                cond = new TernaryExpr(cond, a, b, SpanFrom(first));
            }

            Leave();
            return cond;
        }

        private Expr ParseOr()
        {
            var first = Cur;
            var left = ParseAnd();
            while (Is(ScriptTokenKind.OrOr))
            {
                Advance();
                left = new BinaryExpr(BinaryOperator.Or, left, ParseAnd(), SpanFrom(first));
            }

            return left;
        }

        private Expr ParseAnd()
        {
            var first = Cur;
            var left = ParseEquality();
            while (Is(ScriptTokenKind.AndAnd))
            {
                Advance();
                left = new BinaryExpr(BinaryOperator.And, left, ParseEquality(), SpanFrom(first));
            }

            return left;
        }

        private Expr ParseEquality()
        {
            var first = Cur;
            var left = ParseRelational();
            while (Is(ScriptTokenKind.EqualEqual) || Is(ScriptTokenKind.NotEqual) || Is(ScriptTokenKind.Assign))
            {
                var op = BinaryOperator.Equal;
                if (Is(ScriptTokenKind.NotEqual))
                {
                    op = BinaryOperator.NotEqual;
                }
                else if (Is(ScriptTokenKind.Assign))
                {
                    ReportAt(Cur, ScriptDiagnosticCodes.AssignInExpression, "Single '=' is an assignment: comparison is '=='.");
                }

                Advance();
                left = new BinaryExpr(op, left, ParseRelational(), SpanFrom(first));
            }

            return left;
        }

        private Expr ParseRelational()
        {
            var first = Cur;
            var left = ParseAdditive();
            BinaryOperator? op = Cur.Kind switch
            {
                ScriptTokenKind.Less => BinaryOperator.Less,
                ScriptTokenKind.LessOrEqual => BinaryOperator.LessOrEqual,
                ScriptTokenKind.Greater => BinaryOperator.Greater,
                ScriptTokenKind.GreaterOrEqual => BinaryOperator.GreaterOrEqual,
                _ => null,
            };

            if (op is { } o)
            {
                Advance();
                return new BinaryExpr(o, left, ParseAdditive(), SpanFrom(first));
            }

            if (IsKw("in"))
            {
                Advance();
                Expect(ScriptTokenKind.LParen, "'('");
                var items = new List<Expr> { ParseExpr() };
                while (Is(ScriptTokenKind.Comma))
                {
                    Advance();
                    items.Add(ParseExpr());
                }

                Expect(ScriptTokenKind.RParen, "')'");
                return new InExpr(left, items, SpanFrom(first));
            }

            return left;
        }

        private Expr ParseAdditive()
        {
            var first = Cur;
            var left = ParseMultiplicative();
            while (Is(ScriptTokenKind.Plus) || Is(ScriptTokenKind.Minus))
            {
                var op = Advance().Kind == ScriptTokenKind.Plus ? BinaryOperator.Add : BinaryOperator.Subtract;
                left = new BinaryExpr(op, left, ParseMultiplicative(), SpanFrom(first));
            }

            return left;
        }

        private Expr ParseMultiplicative()
        {
            var first = Cur;
            var left = ParseUnary();
            while (Is(ScriptTokenKind.Star) || Is(ScriptTokenKind.Slash) || Is(ScriptTokenKind.Percent))
            {
                var op = Advance().Kind switch
                {
                    ScriptTokenKind.Star => BinaryOperator.Multiply,
                    ScriptTokenKind.Slash => BinaryOperator.Divide,
                    _ => BinaryOperator.Modulo,
                };
                left = new BinaryExpr(op, left, ParseUnary(), SpanFrom(first));
            }

            return left;
        }

        private Expr ParseUnary()
        {
            var first = Cur;
            if (Is(ScriptTokenKind.Minus) || Is(ScriptTokenKind.Plus) || Is(ScriptTokenKind.Bang))
            {
                var op = Advance().Kind switch
                {
                    ScriptTokenKind.Minus => UnaryOperator.Negate,
                    ScriptTokenKind.Plus => UnaryOperator.Plus,
                    _ => UnaryOperator.Not,
                };
                Enter(first);
                var operand = ParseUnary();
                Leave();
                return new UnaryExpr(op, operand, SpanFrom(first));
            }

            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            var first = Cur;
            var e = ParsePrimary();
            while (true)
            {
                if (Is(ScriptTokenKind.LBracket))
                {
                    Advance();
                    var keys = new List<Expr> { ParseExpr() };
                    while (Is(ScriptTokenKind.Comma))
                    {
                        Advance();
                        keys.Add(ParseExpr());
                    }

                    Expect(ScriptTokenKind.RBracket, "']'");
                    e = new IndexExpr(e, keys, SpanFrom(first));
                }
                else if (Is(ScriptTokenKind.Dot) && PeekTok(1).Kind == ScriptTokenKind.Identifier)
                {
                    Advance();
                    var member = Advance();
                    e = new MemberExpr(e, member.Text, member.Span, SpanFrom(first));
                }
                else
                {
                    return e;
                }
            }
        }

        private bool AdjacentOptionalMarker(ScriptToken lastIdent)
            => Is(ScriptTokenKind.Question) && Cur.Start == lastIdent.Start + lastIdent.Length;

        private Expr ParsePrimary()
        {
            var first = Cur;
            switch (first.Kind)
            {
                case ScriptTokenKind.NumberLiteral:
                    Advance();
                    return new NumberExpr((decimal)first.Value!, first.Span);
                case ScriptTokenKind.StringLiteral:
                    Advance();
                    return new StringExpr((string)first.Value!, first.Span);
                case ScriptTokenKind.LParen:
                {
                    Advance();
                    var inner = ParseExpr();
                    Expect(ScriptTokenKind.RParen, "')'");
                    return inner;
                }

                case ScriptTokenKind.Identifier:
                    return ParseIdentifierPrimary();
                case ScriptTokenKind.Keyword:
                    return ParseKeywordPrimary();
                default:
                    throw FailUnexpected($"Unexpected '{Describe(first)}': an expression was expected.");
            }
        }

        private Expr ParseKeywordPrimary()
        {
            var first = Cur;
            switch (first.Text)
            {
                case "true" or "false":
                    Advance();
                    return new BoolExpr(first.Text == "true", first.Span);
                case "null":
                    Advance();
                    return new NullExpr(first.Span);
                case "row":
                {
                    Advance();
                    Expect(ScriptTokenKind.Dot, "'.'");
                    var field = ExpectIdent("a column code");
                    var optional = false;
                    if (AdjacentOptionalMarker(field))
                    {
                        Advance();
                        optional = true;
                    }

                    return new RowExpr(field.Text, optional, SpanFrom(first));
                }

                case "period":
                {
                    Advance();
                    Expect(ScriptTokenKind.Dot, "'.'");
                    var prop = ExpectIdent("a period property");
                    return new PeriodExpr(prop.Text, SpanFrom(first));
                }

                case "header":
                {
                    Advance();
                    Expect(ScriptTokenKind.Dot, "'.'");
                    var prop = ExpectIdent("a header name");
                    return new HeaderExpr(prop.Text, SpanFrom(first));
                }

                case "entry":
                {
                    var (registry, code) = ParseEntryArgs();
                    return new EntryExpr(registry, code, SpanFrom(first));
                }

                case "key":
                {
                    Advance();
                    Expect(ScriptTokenKind.LParen, "'('");
                    ExpectKw("row");
                    Expect(ScriptTokenKind.Dot, "'.'");
                    var field = ExpectIdent("a column code");
                    var optional = false;
                    if (AdjacentOptionalMarker(field))
                    {
                        Advance();
                        optional = true;
                    }

                    Expect(ScriptTokenKind.RParen, "')'");
                    return new KeyExpr(field.Text, optional, SpanFrom(first));
                }

                default:
                    throw FailUnexpected($"Unexpected '{first.Text}': an expression was expected.");
            }
        }

        private static AggregateKind? AggregateOf(string name) => name switch
        {
            "Sum" => AggregateKind.Sum,
            "Min" => AggregateKind.Min,
            "Max" => AggregateKind.Max,
            "Avg" => AggregateKind.Avg,
            "First" => AggregateKind.First,
            "Count" => AggregateKind.Count,
            "Any" => AggregateKind.Any,
            _ => null,
        };

        private Expr ParseIdentifierPrimary()
        {
            var first = Advance();
            if (Is(ScriptTokenKind.LParen))
            {
                return ParseCall(first, null);
            }

            if (Is(ScriptTokenKind.Dot) && PeekTok(1).Kind == ScriptTokenKind.Identifier
                && PeekTok(2).Kind == ScriptTokenKind.LParen)
            {
                Advance();
                var member = Advance();
                return ParseCall(member, first, first);
            }

            return new NameExpr(first.Text, first.Span);
        }

        private Expr ParseCall(ScriptToken nameTok, ScriptToken? library, ScriptToken? startTok = null)
        {
            var start = startTok ?? nameTok;
            Expect(ScriptTokenKind.LParen, "'('");
            var save = nesting;
            Enter(nameTok);
            var args = new List<Expr>();
            Expr? result = null;
            if (library is null && AggregateOf(nameTok.Text) is { } kind && !Is(ScriptTokenKind.RParen))
            {
                var firstArg = ParseExpr();
                if (IsKw("where"))
                {
                    Advance();
                    var where = ParseExpr();
                    Expect(ScriptTokenKind.RParen, "')'");
                    result = new AggregateExpr(kind, firstArg, where, nameTok.Span, SpanFrom(start));
                }
                else if (Is(ScriptTokenKind.Comma))
                {
                    args.Add(firstArg);
                }
                else
                {
                    Expect(ScriptTokenKind.RParen, "')'");
                    result = new AggregateExpr(kind, firstArg, null, nameTok.Span, SpanFrom(start));
                }
            }

            if (result is null)
            {
                if (args.Count > 0)
                {
                    while (Is(ScriptTokenKind.Comma))
                    {
                        Advance();
                        args.Add(ParseExpr());
                    }
                }
                else if (!Is(ScriptTokenKind.RParen))
                {
                    args.Add(ParseExpr());
                    while (Is(ScriptTokenKind.Comma))
                    {
                        Advance();
                        args.Add(ParseExpr());
                    }
                }

                Expect(ScriptTokenKind.RParen, "')'");
                result = library is { } lib
                    ? new LibCallExpr(lib.Text, nameTok.Text, args, nameTok.Span, SpanFrom(start))
                    : new CallExpr(nameTok.Text, args, nameTok.Span, SpanFrom(start));
            }

            nesting = save;
            return result;
        }
    }
}

/// <summary>Текстові подання чисел для ключів мап.</summary>
internal static class ScriptNumbers
{
    /// <summary>Інваріантний текст числа без хвостових нулів (<c>1.50</c> → <c>1.5</c>).</summary>
    public static string KeyText(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text == "-0" ? "0" : text;
    }
}
