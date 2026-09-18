using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Жодного шляху рекурсії ОБЧИСЛЮВАЧА не лишилося без межі глибини.
/// </summary>
/// <remarks>
/// ⛔ Це другий примірник сторожа, який уже стоїть над парсером
/// (<see cref="ParserRecursionCoverageTests"/>), і він з'явився не з симетрії, а
/// тому, що дефект повторився на сусідньому поверсі. Межа глибини парсера
/// рахує спуски ПІД ЧАС РОЗБОРУ, а плаский ланцюг <c>1+1+…+1</c> розбирається
/// циклом: глибина розбору лишається O(1), а дерево виходить лівим гребенем
/// глибиною в кількість доданків. Обчислювач ішов цим гребенем рекурсивно й
/// помирав — <c>0xC00000FD</c>, і <c>StackOverflowException</c> у .NET не
/// перехоплюється, тобто падав увесь хост перерахунку.
///
/// ⚠ Тест не перелічує шляхи, а БУДУЄ граф викликів обчислювача з його ж
/// тексту, викидає з нього кожен метод, що заходить у <c>EnterNesting</c> перед
/// своїм першим рекурсивним викликом, і вимагає, щоб залишок був АЦИКЛІЧНИМ.
/// Перелік шляхів перевіряв би ту саму пам'ять, яка їх і пропустила: наступне
/// рекурсивне ребро (а мова росте) знову нікому не впало б в око.
/// </remarks>
public sealed partial class EvaluatorRecursionCoverageTests
{
    private const string EvaluatorFile = "src/Ecr.Expressions/Evaluation/Evaluator.cs";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Кожен_цикл_у_графі_викликів_обчислювача_проходить_крізь_сторожа_глибини()
    {
        var methods = Methods();

        // Сам граф мусить існувати: порожній граф ациклічний за визначенням і
        // зробив би перевірку вічнозеленою, якби Evaluator.cs перейменували.
        Assert.True(methods.Count >= 10, $"Знайдено лише {methods.Count} методів обчислення.");
        Assert.Contains("EvaluateScalar", methods.Keys);
        Assert.Contains("Binary", methods.Keys);
        Assert.Contains("TemplateCall", methods.Keys);

        // ⛔ «Містить EnterNesting» — НЕ те саме, що «обмежений»: сторож
        // усередині `if` лишив би вільною гілку повз цей `if`, а метод виглядав
        // би обмеженим. Саме так перша редакція сторожа ПАРСЕРА пропустила
        // `2^2^2…`. Тому умова строга: сторож мусить спрацювати ДО першого
        // рекурсивного виклику.
        var guarded = methods
            .Where(m => Guards(m.Value.Body))
            .Select(m => m.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(guarded);

        var edges = methods
            .Where(m => !guarded.Contains(m.Key))
            .ToDictionary(
                m => m.Key,
                m => m.Value.Calls.Where(c => !guarded.Contains(c) && methods.ContainsKey(c))
                      .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        var cycle = FindCycle(edges);

        Assert.True(
            cycle is null,
            $"Рекурсія повз сторожа глибини: {string.Join(" → ", cycle ?? [])}. "
            + "Такий цикл — необмежений рекурсивний спуск, тобто StackOverflowException, "
            + "який у .NET не перехоплюється і валить увесь процес.");
    }

    /// <summary>
    /// Чи заходить метод у <c>EnterNesting</c> беззастережно — до першого
    /// власного рекурсивного виклику.
    /// </summary>
    private static bool Guards(string body)
    {
        var guard = body.IndexOf("budget.EnterNesting()", StringComparison.Ordinal);
        if (guard < 0)
        {
            return false;
        }

        var firstCall = MethodCall().Match(body);
        return !firstCall.Success || guard < firstCall.Index;
    }

    /// <summary>Методи обчислення з їхніми тілами й викликами сусідів.</summary>
    /// <remarks>
    /// ⚠ У граф потрапляють рівно ті методи, що несуть <c>EvaluationBudget</c>:
    /// бюджет заводиться на вході в обчислення і передається кожним рекурсивним
    /// ребром, тож «метод із бюджетом» і є «метод, здатний спуститися глибше».
    /// Статичні помічники (<c>Power</c>, <c>Compare</c>, <c>AreEqual</c>) бюджету
    /// не бачать і рекурсувати не можуть — компілятор за цим і стежить.
    /// </remarks>
    private static Dictionary<string, (string Body, HashSet<string> Calls)> Methods()
    {
        var text = SourceTree.Production("Ecr.Expressions")
            .Single(f => string.Equals(f.Path, EvaluatorFile, StringComparison.Ordinal))
            .Text;

        // ⚠ Межі методу визначаються наступним оголошенням, а не підрахунком
        // дужок: тіла містять і дужки в рядкових літералах, і `{` у
        // XML-документації, і лічильник збився б на першому ж із них.
        var declarations = MethodDeclaration().Matches(text)
            .Select(m => (Name: m.Groups[1].Value, Start: m.Index))
            .ToList();

        var methods = new Dictionary<string, (string, HashSet<string>)>(StringComparer.Ordinal);

        for (var i = 0; i < declarations.Count; i++)
        {
            var (name, start) = declarations[i];
            var end = i + 1 < declarations.Count ? declarations[i + 1].Start : text.Length;

            // ⛔ Коментарі викидаються ДО пошуку викликів. Інакше рядок
            // документації `<c>Binary(…, budget)</c>` створював би ребро, якого
            // в коді немає, а закоментований виклик — ребро, яке вже прибрали.
            var body = Uncommented(text[start..end]);

            // ⚠ Самовиклик НЕ відкидається: це найкоротший з можливих циклів.
            var calls = MethodCall().Matches(body)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            methods[name] = (body, calls);
        }

        return methods;
    }

    /// <summary>Текст без рядкових коментарів і без XML-документації.</summary>
    private static string Uncommented(string body)
        => string.Concat(body
            .Split('\n')
            .Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return (comment < 0 ? line : line[..comment]) + "\n";
            }));

    /// <summary>Будь-який цикл у графі, або <c>null</c>, якщо граф ациклічний.</summary>
    private static List<string>? FindCycle(Dictionary<string, HashSet<string>> edges)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = new List<string>();

        bool Visit(string node)
        {
            state[node] = 1;
            path.Add(node);

            var outgoing = edges.TryGetValue(node, out var list)
                ? (IEnumerable<string>)list
                : Array.Empty<string>();

            foreach (var next in outgoing)
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

    /// <summary>Оголошення методу, який несе бюджет: <c>… Xxx(… EvaluationBudget budget)</c>.</summary>
    [GeneratedRegex(@"(?m)^\s*(?:public|private|internal)[^\n(]*?\b(\w+)\(\s*[^()]*EvaluationBudget budget\)")]
    private static partial Regex MethodDeclaration();

    /// <summary>
    /// Виклик сусіда з тим самим бюджетом: <c>Xxx(…, budget)</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Кома перед <c>budget</c> обов'язкова — вона й відрізняє ВИКЛИК
    /// (<c>Binary(node, context, dialect, budget)</c>) від ОГОЛОШЕННЯ
    /// (<c>… ExpressionDialect dialect, EvaluationBudget budget)</c>). Без неї
    /// кожне оголошення читалося б як виклик у нульовій позиції тіла, і жоден
    /// метод ніколи не вважався б обмеженим.
    /// </remarks>
    [GeneratedRegex(@"\b(\w+)\((?:[^()]|\([^()]*\))*?,\s*budget\)")]
    private static partial Regex MethodCall();
}
