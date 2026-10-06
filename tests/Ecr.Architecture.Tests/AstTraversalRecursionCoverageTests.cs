using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Жоден рекурсивний обхід дерева виразу не лишився без межі глибини.
/// </summary>
/// <remarks>
/// ⛔ L7-01 (аудит 2026-10-03): ланцюг <c>1+1+…+1</c> розбирається циклом, тож
/// межа глибини ПАРСЕРА мовчить, а дерево виходить лівим гребенем глибиною в
/// кількість ланок. Кожен рекурсивний обхід такого дерева (перевірка типів,
/// одиниць, витяг залежностей, друк, переклад у Excel, класифікація формул)
/// ішов цим гребенем і вичерпував стек; <c>StackOverflowException</c> у .NET
/// не перехоплюється — падав увесь процес. <c>Parser.MaxChainLinks</c>
/// стиснув глибину до ~2 250 рівнів, але не до нуля, і обходи в
/// <c>Ecr.Expressions/Binding</c> отримали сторожа стека. Обходи того самого
/// дерева в інших проєктах (<c>Ecr.Application</c>, <c>Ecr.Calculations</c>,
/// <c>Ecr.Adapters.Excel</c>) — ні: про них просто не згадали.
///
/// ⚠ Тому тест не перелічує відомі обходи, а знаходить їх сам: у кожному
/// файлі <c>src/</c> бере методи, що приймають вузол дерева (<c>AstNode</c> чи
/// його нащадка), будує між ними граф викликів, викидає з нього методи, що
/// перевіряють стек ДО першого рекурсивного виклику, і вимагає, щоб залишок
/// був АЦИКЛІЧНИМ. Новий рекурсивний обхід без сторожа червонить тест,
/// незалежно від того, чи хтось про нього подумав.
///
/// Сторожем вважається будь-що з:
/// <c>TraversalStackGuard.TryEnter(…)</c>,
/// <c>RuntimeHelpers.EnsureSufficientExecutionStack()</c> /
/// <c>TryEnsureSufficientExecutionStack()</c>, <c>….EnterNesting()</c>.
/// Парсер і обчислювач мають власних, строгіших сторожів
/// (<see cref="ParserRecursionCoverageTests"/>,
/// <see cref="EvaluatorRecursionCoverageTests"/>), тож тут їх пропущено.
///
/// ⚠ Межа методу: граф будується в межах ОДНОГО файлу. Рекурсія крізь два
/// класи в різних файлах (A.Check → B.Check → A.Check) тут не видна; таких
/// обходів у дереві немає, а з'являться — сторож треба розширити.
/// </remarks>
public sealed partial class AstTraversalRecursionCoverageTests
{
    private const string AstFile = "src/Ecr.Expressions/Ast/AstNode.cs";

    /// <summary>Файли з власними сторожами рекурсії (див. remarks класу).</summary>
    private static readonly HashSet<string> OwnGuardFiles = new(StringComparer.Ordinal)
    {
        "src/Ecr.Expressions/Parsing/Parser.cs",
        "src/Ecr.Expressions/Evaluation/Evaluator.cs",
    };

    private static readonly string[] GuardTokens =
    [
        "TraversalStackGuard.TryEnter(",
        "EnsureSufficientExecutionStack()",
        ".EnterNesting()",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Кожен_цикл_рекурсивного_обходу_дерева_виразу_проходить_крізь_сторожа_стека()
    {
        var nodeTypes = NodeTypes();
        var graphs = SourceTree.Production()
            .Where(f => !OwnGuardFiles.Contains(f.Path))
            .Select(f => (f.Path, Methods: Methods(f, nodeTypes)))
            .Where(g => g.Methods.Count > 0)
            .ToList();

        // ⛔ Сам граф мусить існувати: якби регулярні вирази перестали бачити
        // оголошення (зміна стилю, перейменування AstNode), граф спорожнів би, а
        // порожній граф ациклічний за визначенням — тест став би вічнозеленим.
        // Тому тест спершу впізнає обходи, про які відомо напевно.
        var recursive = graphs
            .SelectMany(g => Recursive(g.Methods).Select(m => $"{g.Path}:{m.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("src/Ecr.Expressions/Ast/AstPrinter.cs:Write", recursive);
        Assert.Contains("src/Ecr.Expressions/Binding/TypeChecker.cs:Infer", recursive);
        Assert.Contains("src/Ecr.Expressions/Binding/UnitChecker.cs:HasRowScopedUnit", recursive);
        Assert.Contains("src/Ecr.Expressions/Binding/DependencyExtractor.cs:Visit", recursive);
        Assert.True(recursive.Count >= 15, $"Знайдено лише {recursive.Count} рекурсивних обходів дерева.");

        var violations = new List<string>();

        foreach (var (path, methods) in graphs)
        {
            // Граф без сторожів: лишаються переходи, які НЕ обмежені нічим.
            var edges = Enumerable.Range(0, methods.Count)
                .Where(i => !methods[i].Guarded)
                .ToDictionary(
                    i => i,
                    i => methods[i].Calls.Where(j => !methods[j].Guarded).ToHashSet());

            if (FindCycle(edges) is { } cycle)
            {
                violations.Add($"{path}: {string.Join(" → ", cycle.Select(i => methods[i].Name))}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Рекурсивний обхід дерева виразу повз сторожа стека (L7-01):\n  "
            + string.Join("\n  ", violations)
            + "\nЛівий гребінь ланцюга 1+1+…+1 має глибину в тисячі рівнів; без "
            + "RuntimeHelpers.EnsureSufficientExecutionStack() (або TraversalStackGuard.TryEnter) "
            + "на вході методу, ДО першого рекурсивного виклику, це StackOverflowException, "
            + "який у .NET не перехоплюється і валить увесь процес.");
    }

    /// <summary>Імена типів вузлів дерева: <c>AstNode</c> і його нащадки.</summary>
    private static HashSet<string> NodeTypes()
    {
        var text = SourceTree.Production("Ecr.Expressions")
            .Single(f => string.Equals(f.Path, AstFile, StringComparison.Ordinal))
            .Text;

        var types = NodeTypeDeclaration().Matches(text)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        types.Add("AstNode");

        Assert.True(types.Count >= 8, $"Знайдено лише {types.Count} типів вузлів у {AstFile}.");
        return types;
    }

    /// <summary>
    /// Методи файлу, що приймають вузол дерева, з викликами одне одного й
    /// ознакою сторожа.
    /// </summary>
    private static List<Method> Methods(SourceFile file, HashSet<string> nodeTypes)
    {
        // ⛔ Коментарі й літерали викидаються ДО пошуку: документація
        // `<c>Write(node)</c>` інакше дала б ребро, якого в коді немає, а дужка
        // в рядку `"("` збила б підрахунок аргументів.
        var text = CharLiteral().Replace(
            string.Join('\n', file.CodeLines().Select(l => l.Text)),
            "' '");

        // ⚠ Межі методу — наступне оголошення, а не підрахунок дужок; тіло
        // методу, що не приймає вузол, просто не потрапляє в граф.
        var declarations = MethodDeclaration().Matches(text).ToList();
        var all = new List<(string Name, string Parameters, string Body)>();

        for (var i = 0; i < declarations.Count; i++)
        {
            var match = declarations[i];
            var end = i + 1 < declarations.Count ? declarations[i + 1].Index : text.Length;
            all.Add((match.Groups[1].Value, match.Groups[2].Value, text[(match.Index + match.Length)..end]));
        }

        var nodes = all
            .Where(m => SplitTopLevel(m.Parameters).Any(p => TypeOf(p, nodeTypes)))
            .ToList();

        if (nodes.Count == 0)
        {
            return [];
        }

        var signatures = nodes.Select(m => Arity(m.Parameters)).ToList();
        var methods = new List<Method>();

        foreach (var (name, _, body) in nodes)
        {
            var calls = new List<(int Position, int Target)>();

            foreach (Match call in MethodCall().Matches(body))
            {
                var open = body.IndexOf('(', call.Index + call.Groups[1].Length);
                var arguments = SplitTopLevel(ArgumentsAt(body, open)).Count;

                // ⚠ Перевантаження розрізняються числом аргументів: інакше
                // публічний `Check(node, ctx, list)`, що кличе приватний
                // `Check(node, ctx, list, row)`, читався б як самовиклик без
                // сторожа.
                for (var j = 0; j < nodes.Count; j++)
                {
                    if (string.Equals(nodes[j].Name, call.Groups[1].Value, StringComparison.Ordinal)
                        && signatures[j].Min <= arguments && arguments <= signatures[j].Max)
                    {
                        calls.Add((call.Index, j));
                    }
                }
            }

            // ⛔ Група методів — теж виклик: `function.Arguments.All(IsLocal)`
            // рекурсує так само, як `IsLocal(argument)`, а дужок після імені не має.
            foreach (Match group in MethodGroup().Matches(body))
            {
                for (var j = 0; j < nodes.Count; j++)
                {
                    if (string.Equals(nodes[j].Name, group.Groups[1].Value, StringComparison.Ordinal))
                    {
                        calls.Add((group.Index, j));
                    }
                }
            }

            // ⛔ Сторож усередині `if` чи ПІСЛЯ рекурсивного виклику не обмежує
            // глибину: гілка повз нього лишається вільною. Так перша редакція
            // сторожа парсера пропустила `2^2^2…`. Тому умова строга — сторож
            // ДО першого виклику сусіда з графа.
            var firstCall = calls.Count == 0 ? int.MaxValue : calls.Min(c => c.Position);
            var guard = GuardTokens
                .Select(t => body.IndexOf(t, StringComparison.Ordinal))
                .Where(p => p >= 0)
                .DefaultIfEmpty(-1)
                .Min();

            methods.Add(new Method(name, guard >= 0 && guard < firstCall, calls.Select(c => c.Target).ToHashSet()));
        }

        return methods;
    }

    /// <summary>Методи, що лежать на якомусь циклі графа (зі сторожами включно).</summary>
    private static IEnumerable<Method> Recursive(List<Method> methods)
    {
        for (var i = 0; i < methods.Count; i++)
        {
            var seen = new HashSet<int>();
            var pending = new Stack<int>(methods[i].Calls);

            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (seen.Add(next))
                {
                    foreach (var call in methods[next].Calls)
                    {
                        pending.Push(call);
                    }
                }
            }

            if (seen.Contains(i))
            {
                yield return methods[i];
            }
        }
    }

    /// <summary>Будь-який цикл у графі, або <c>null</c>, якщо граф ациклічний.</summary>
    private static List<int>? FindCycle(Dictionary<int, HashSet<int>> edges)
    {
        var state = new Dictionary<int, int>();
        var path = new List<int>();

        bool Visit(int node)
        {
            state[node] = 1;
            path.Add(node);

            foreach (var next in edges.TryGetValue(node, out var list) ? list : [])
            {
                var seen = state.GetValueOrDefault(next);
                if (seen == 1)
                {
                    path.Add(next);
                    return true;
                }

                if (seen == 0 && Visit(next))
                {
                    return true;
                }
            }

            state[node] = 2;
            path.RemoveAt(path.Count - 1);
            return false;
        }

        foreach (var node in edges.Keys)
        {
            if (state.GetValueOrDefault(node) == 0 && Visit(node))
            {
                return path;
            }
        }

        return null;
    }

    private static bool TypeOf(string parameter, HashSet<string> nodeTypes)
        => Identifier().Matches(parameter).Any(m => nodeTypes.Contains(m.Value));

    /// <summary>Скільки аргументів приймає метод: обов'язкові … усі (<c>params</c> — без стелі).</summary>
    private static (int Min, int Max) Arity(string parameters)
    {
        var list = SplitTopLevel(parameters);
        var required = list.Count(p => !p.Contains('=', StringComparison.Ordinal)
                                       && !p.TrimStart().StartsWith("params ", StringComparison.Ordinal));
        var unbounded = list.Exists(p => p.TrimStart().StartsWith("params ", StringComparison.Ordinal));
        return (required, unbounded ? int.MaxValue : list.Count);
    }

    /// <summary>Вміст дужок, що відкриваються в позиції <paramref name="open"/>.</summary>
    private static string ArgumentsAt(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return text[(open + 1)..i];
            }
        }

        return string.Empty;
    }

    /// <summary>Елементи списку через кому верхнього рівня (без ком у дужках і узагальненнях).</summary>
    private static List<string> SplitTopLevel(string list)
    {
        var items = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < list.Length; i++)
        {
            switch (list[i])
            {
                case '(' or '<' or '[' or '{':
                    depth++;
                    break;
                case ')' or '>' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    items.Add(list[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(list[start..]))
        {
            items.Add(list[start..]);
        }

        return items;
    }

    private sealed record Method(string Name, bool Guarded, HashSet<int> Calls);

    /// <summary>Оголошення вузла: <c>record XxxNode</c> / <c>class XxxNode</c>.</summary>
    [GeneratedRegex(@"\b(?:record|class)\s+(\w+Node)\b")]
    private static partial Regex NodeTypeDeclaration();

    /// <summary>
    /// Оголошення методу чи локальної функції: <c>[модифікатори] Тип Ім'я(параметри)</c>,
    /// за яким іде тіло (<c>{</c>, <c>=&gt;</c> чи перенос рядка).
    /// </summary>
    /// <remarks>
    /// ⚠ Ключові слова на місці типу (<c>return Foo(x)</c>, <c>new Foo(x)</c>)
    /// виключено явно — інакше кожен виклик у <c>return</c> читався б як
    /// оголошення і рвав би тіло методу навпіл.
    /// </remarks>
    [GeneratedRegex(@"(?m)^[ \t]*(?:(?:public|private|internal|protected|static|override|virtual|sealed|async|unsafe|new|partial|abstract|extern)\s+)*(?!(?:return|await|new|throw|yield|else|case|using|lock|when|is|and|or|not)\b)[\w.?\[\]]+(?:<[^()\n]*>)?\??\s+(\w+)\s*(?:<[^<>()]*>)?\(((?:[^()]|\([^()]*\))*)\)\s*(?:where[^\n{]*)?(?:=>|\{|$)")]
    private static partial Regex MethodDeclaration();

    /// <summary>Виклик методу без отримувача: <c>Xxx(</c>, але не <c>obj.Xxx(</c>.</summary>
    [GeneratedRegex(@"(?<![.\w])(\w+)\s*(?:<[\w<>, ?]*>)?\(")]
    private static partial Regex MethodCall();

    /// <summary>Група методів як аргумент: <c>.All(IsLocal)</c>, <c>.SelectMany(Walk)</c>.</summary>
    [GeneratedRegex(@"(?<=[(,]\s*)(\w+)(?=\s*[,)])")]
    private static partial Regex MethodGroup();

    [GeneratedRegex(@"'(?:\\.|[^'\\\n])'")]
    private static partial Regex CharLiteral();

    [GeneratedRegex(@"\w+")]
    private static partial Regex Identifier();
}
