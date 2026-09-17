using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Жодного шляху рекурсії парсера не лишилося без межі глибини.
/// </summary>
/// <remarks>
/// ⛔ Це сторож саме проти того способу, яким дефект і виник. Межу легко
/// поставити там, де про неї згадали (дужки), і не поставити там, де про неї
/// не згадали — <c>NOT NOT …</c>, <c>- - …</c>, правоасоціативний
/// <c>2^2^2…</c>. Перелічити ці випадки в тесті означало б перевіряти ту саму
/// пам'ять, яка їх і пропустила: наступне рекурсивне ребро граматики
/// (а їх додають щоразу, коли мова росте) знову нікому не впаде в око.
///
/// ⚠ Тому тест не перелічує шляхи, а БУДУЄ граф викликів парсера з його ж
/// тексту, викидає з нього кожен метод, що заходить у <c>EnterNesting</c>, і
/// вимагає, щоб залишок був АЦИКЛІЧНИМ. Ациклічний залишок означає рівно одне:
/// будь-який цикл у графі обов'язково проходить крізь сторожа, тобто
/// необмеженої рекурсії в парсері не існує — незалежно від того, чи хтось про
/// неї подумав.
/// </remarks>
public sealed partial class ParserRecursionCoverageTests
{
    private const string ParserFile = "src/Ecr.Expressions/Parsing/Parser.cs";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Кожен_цикл_у_графі_викликів_парсера_проходить_крізь_сторожа_глибини()
    {
        var methods = Methods();

        // Сам граф мусить існувати: порожній граф ациклічний за визначенням і
        // зробив би перевірку вічнозеленою, якби Parser.cs перейменували.
        Assert.True(methods.Count >= 12, $"Знайдено лише {methods.Count} методів розбору.");
        Assert.Contains("ParseExpression", methods.Keys);
        Assert.Contains("ParsePrimary", methods.Keys);

        // ⛔ «Містить EnterNesting» — НЕ те саме, що «обмежений», і перша
        // редакція цього фіксу спіткнулася саме об це: сторож стояв усередині
        // `if`, гілка повз `if` (`2^2^2…`) лишалася вільною, а тест був
        // зелений. Тому умова строга: сторож мусить спрацювати ДО першого
        // рекурсивного виклику методу — інакше метод не вважається обмеженим і
        // лишається в графі.
        var guarded = methods
            .Where(m => Guards(m.Value.Body))
            .Select(m => m.Key)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(guarded);

        // Граф без сторожів: залишаються лише переходи, які НЕ обмежені нічим.
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
        var guard = body.IndexOf("s.EnterNesting()", StringComparison.Ordinal);
        if (guard < 0)
        {
            return false;
        }

        var firstCall = MethodCall().Match(body);
        return !firstCall.Success || guard < firstCall.Index;
    }

    /// <summary>Методи розбору з їхніми тілами й викликами сусідів.</summary>
    private static Dictionary<string, (string Body, HashSet<string> Calls)> Methods()
    {
        var text = SourceTree.Production("Ecr.Expressions")
            .Single(f => string.Equals(f.Path, ParserFile, StringComparison.Ordinal))
            .Text;

        // ⚠ Межі методу визначаються наступним оголошенням, а не підрахунком
        // дужок: тіла тут містять і дужки в рядкових літералах, і `{` у
        // XML-документації, і лічильник збився б на першому ж `Expected ")"`.
        var declarations = MethodDeclaration().Matches(text)
            .Select(m => (Name: m.Groups[1].Value, Start: m.Index))
            .ToList();

        var methods = new Dictionary<string, (string, HashSet<string>)>(StringComparer.Ordinal);

        for (var i = 0; i < declarations.Count; i++)
        {
            var (name, start) = declarations[i];
            var end = i + 1 < declarations.Count ? declarations[i + 1].Start : text.Length;
            var body = text[start..end];

            // ⚠ Самовиклик (`ParseNot(s)` всередині `ParseNot`) НЕ відкидається:
            // це найкоротший з можливих циклів і рівно той, яким `NOT NOT …`
            // клав процес.
            var calls = MethodCall().Matches(body)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            methods[name] = (body, calls);
        }

        return methods;
    }

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

    /// <summary>Оголошення методу розбору: <c>private static … ParseXxx(State s)</c>.</summary>
    [GeneratedRegex(@"(?m)^\s*private static [^\n(]*?\b(Parse\w+|Symbol)\(State s[,)]")]
    private static partial Regex MethodDeclaration();

    /// <summary>Виклик сусіднього методу розбору з тим самим станом.</summary>
    [GeneratedRegex(@"\b(Parse\w+|Symbol)\(s[,)]")]
    private static partial Regex MethodCall();
}
