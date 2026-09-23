using System.Globalization;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Тест еквівалентності клієнт/сервер.
/// </summary>
/// <remarks>
/// Клієнтський обчислювач (<c>formulajs</c>) — **лише підказка** під час
/// введення; збережене значення завжди рахує сервер (D-20). Але якщо підказка
/// систематично розходиться з результатом, користувач перестає їй вірити —
/// і саме тому набір спільних випадків має збігатися.
///
/// Реалізація: набір виразів і очікувань зберігається у спільному JSON, який
/// читають і цей тест, і vitest-тест на клієнті (див. `06e`).
/// </remarks>
public sealed class ClientServerEquivalenceTests
{
    private static readonly JsonElement Fixture = Load();

    private static readonly JsonElement Cases = Fixture.GetProperty("cases");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Спільний_набір_виразів_дає_однакові_результати_на_сервері()
    {
        var failures = new List<string>();

        foreach (var item in Cases.EnumerateArray())
        {
            var expression = item.GetProperty("expression").GetString()!;
            var kind = item.GetProperty("kind").GetString()!;
            var expected = item.GetProperty("expected").GetString()!;

            var value = Expr.Eval(expression);

            var actual = value.Type switch
            {
                ExpressionValueType.Null => ("null", string.Empty),
                ExpressionValueType.Error => ("error", value.ErrorCode!),
                ExpressionValueType.Number => ("number", ((decimal)value.Value!).ToString(CultureInfo.InvariantCulture)),
                ExpressionValueType.Boolean => ("boolean", (bool)value.Value! ? "true" : "false"),
                _ => ("text", value.Value?.ToString() ?? string.Empty),
            };

            if (actual.Item1 != kind || !Same(kind, actual.Item2, expected))
            {
                failures.Add($"{expression} → {actual.Item1}:{actual.Item2}, очікувалося {kind}:{expected}");
            }
        }

        // ⚠ Перевіряється ВЕСЬ набір, а не «до першої розбіжності»: якщо
        // клієнт і сервер розійшлися, треба бачити ВСІ місця розходження, бо
        // вони майже завжди одного роду.
        Assert.Empty(failures);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Набір_покриває_усі_функції_діалекту_шаблонів()
    {
        var covered = Cases.EnumerateArray()
            .Select(c => c.GetProperty("function").GetString()!)
            .Where(name => name != "-")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var declared = FunctionRegistry.Names;

        // ⚠ CONVERT і REGFIELD — єдині винятки, і не за недоглядом.
        // CONVERT потребує довідника uom, якого на клієнті немає й не буде.
        // REGFIELD (2026-09-23) потребує і резолвінгу посилання на комірку, і
        // даних довідника — клієнт не резолвить посилань на комірки взагалі
        // («клієнт не резолвить посилань на комірки» — коментар нижче,
        // `evaluate.ts`), тож підказка під час введення однаково не порахує
        // REGFIELD, і розходитися тут нічому так само, як із CONVERT.
        var shared = declared.Except(["CONVERT", "REGFIELD"], StringComparer.OrdinalIgnoreCase);

        // Функція, якої немає в наборі, — це функція, чию поведінку клієнт і
        // сервер ніде не звіряють. Саме там і з'явиться перше розходження.
        Assert.Equal(13, declared.Count);
        Assert.Empty(shared.Except(covered, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Найдорожчий_клієнтський_вираз_не_наближається_до_бюджету_сервера()
    {
        // ⛔ ЧОМУ ЦЕ ТУТ, А НЕ В EvaluationBudgetTests. Це твердження не про
        // бюджет — воно про РОЗБІЖНІСТЬ клієнта й сервера, тобто про те саме,
        // заради чого існує весь цей файл. Сервер зупиняє обчислення на
        // 20 000 кроків і віддає `#BUDGET` (02b §6.5); клієнтський обчислювач
        // такої межі не має і мати не буде. Небезпека не в тому, що клієнт
        // «підвисне», а в тому, що він покаже ЧИСЛО там, де сервер віддасть
        // `#BUDGET`: користувач побачить одне, а в документ поїде інше.
        //
        // ⚠ Доводиться саме недосяжність, а не «навряд чи трапиться». Клієнт
        // не резолвить посилань на комірки (`evaluate.ts`, `parsePrimary` →
        // `#NAME`), тож дорогою для сервера частиною — читанням рядків —
        // розійтися неможливо: клієнт до неї не доходить. Лишаються літерали,
        // і їхня ціна впирається в довжину тексту, яку сервер сам і обмежує
        // (2000 символів, `cfg.FormulaDef.Expression`). Щільніше за ~один
        // вузол AST на символ вираз не буває, тому межа лишається
        // недосяжною з названим запасом.
        // ⛔ Форма виразу — `SUM(1,1,…)`, а не щільніше `1+1+…`, і причина
        // названа в `$comment` фікстури: плаский ланцюг `+` на 1000 доданків
        // (1999 символів, припустимий за ВСІМА чинними правилами) переповнює
        // стек ЦЬОГО обчислювача — `Binary` → `EvaluateScalar` рекурсією по
        // лівому ребру, 707 повторів, «Test host process crashed: Stack
        // overflow». Це окремий дефект доступності, і ловити його тестом тут
        // не можна: `StackOverflowException` не перехоплюється, тож тест не
        // почервонів би, а вбив хост (преамбула `ExpressionDepthGuardTests`).
        var edge = Fixture.GetProperty("budgetEdge");
        var expression = edge.GetProperty("prefix").GetString()!
            + string.Join(
                edge.GetProperty("separator").GetString()!,
                Enumerable.Repeat(edge.GetProperty("repeat").GetString()!, edge.GetProperty("times").GetInt32()))
            + edge.GetProperty("suffix").GetString()!;

        // ⛔ Та сама звірка довжини, що й на клієнті. Обидва боки будують текст
        // із полів фікстури; якби вони зібрали різне, кожен доводив би своє про
        // свій вираз — і «узгодженість» була б словом, а не перевіркою.
        Assert.Equal(edge.GetProperty("chars").GetInt32(), expression.Length);

        var value = Eval(expression, out var spent);

        // Сервер дає рівно те, що фікстура обіцяє клієнтові — тобто НЕ `#BUDGET`.
        Assert.Equal(ExpressionValueType.Number, value.Type);
        Assert.Equal(
            decimal.Parse(edge.GetProperty("expected").GetString()!, CultureInfo.InvariantCulture),
            (decimal)value.Value!);

        // ⚠ Запас названий ЧИСЛОМ, а не словом. Якщо бюджет колись опустять до
        // цієї ціни — або граматику змінять так, що символ почне коштувати
        // більше одного кроку, — тест скаже це прямо, а не через скаргу
        // користувача на два різні числа.
        Assert.True(
            spent * 16 <= Evaluator.MaxEvaluationSteps,
            $"Найдорожчий вираз, доступний клієнтові ({expression.Length} символів), коштує серверу "
            + $"{spent} кроків із бюджету {Evaluator.MaxEvaluationSteps} — запас менший за "
            + "шістнадцятикратний. Щойно він зникне, клієнт почне показувати число там, де сервер "
            + "віддає #BUDGET: користувач побачить одне, а в документ поїде інше.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Жоден_вираз_спільного_набору_не_наближається_до_бюджету_сервера()
    {
        // ⚠ Те саме твердження, але про ВЕСЬ спільний набір, а не про один
        // крайній випадок: кожен вираз, який клієнт і сервер зобов'язані
        // рахувати однаково, мусить лишатися далеко від межі. Так перевірка не
        // застаріє від додавання нового випадку — новий випадок теж міряється.
        var worst = 0;
        var worstText = string.Empty;

        foreach (var item in Cases.EnumerateArray())
        {
            var expression = item.GetProperty("expression").GetString()!;
            Eval(expression, out var spent);

            if (spent > worst)
            {
                worst = spent;
                worstText = expression;
            }
        }

        Assert.True(worst > 0);
        Assert.True(
            worst * 100 <= Evaluator.MaxEvaluationSteps,
            $"Найдорожчий вираз спільного набору коштує {worst} кроків із бюджету "
            + $"{Evaluator.MaxEvaluationSteps} — «{worstText}». Набір еквівалентності має жити далеко "
            + "від межі, якої клієнт не бачить.");
    }

    /// <summary>Обчислює вираз під бюджетом сервера, віддаючи витрачені кроки.</summary>
    private static ExpressionValue Eval(string expression, out int spent)
    {
        var parsed = Expr.Parse(expression);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var budget = new EvaluationBudget(Evaluator.MaxEvaluationSteps);
        var context = new TestEvaluationContext { Budget = budget };

        var value = new Evaluator(new FunctionRegistry())
            .Evaluate(parsed.Expression!.Root, context, ExpressionDialect.Template, budget);

        spent = budget.Spent;

        return value;
    }

    /// <summary>Числа порівнюються як <c>decimal</c>, решта — як текст.</summary>
    private static bool Same(string kind, string actual, string expected)
        => kind == "number"
            ? decimal.Parse(actual, CultureInfo.InvariantCulture)
              == decimal.Parse(expected, CultureInfo.InvariantCulture)
            : string.Equals(actual, expected, StringComparison.Ordinal);

    private static JsonElement Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expression-equivalence.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
