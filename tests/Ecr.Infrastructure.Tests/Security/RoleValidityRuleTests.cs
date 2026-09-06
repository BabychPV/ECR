// tests/Ecr.Infrastructure.Tests/Security/RoleValidityRuleTests.cs
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// Правило меж дії призначення ролі сформульоване РІВНО ОДИН раз
/// (<c>H-23a</c>).
/// </summary>
/// <remarks>
/// ⛔ Тут навмисно перевіряється ВИХІДНИЙ КОД, а не поведінка, і причина в
/// самій природі знахідки. Копія умови в запиті збігалася з доменним методом
/// ДОСЛІВНО, тож жоден поведінковий тест не міг їх розрізнити: обидва
/// формулювання давали ту саму відповідь на будь-яких даних. Небезпека була не
/// в розбіжності, а в її неминучості — правку зробили б в одному місці, і
/// розійшлися б вони мовчки, бо друге місце ніхто не шукав би.
///
/// ⚠ Ціна розходження асиметрична: якщо помиляється домен, права зникають і
/// про це кажуть того ж дня; якщо помиляється запит, права ЛИШАЮТЬСЯ після
/// закінчення підміни — і не каже ніхто.
/// </remarks>
public sealed partial class RoleValidityRuleTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23a")]
    public void Побудова_профілю_питає_домен_а_не_повторює_його_умову()
    {
        var source = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Security", "AccessDecisionService.cs"));

        var executable = Comments().Replace(source, string.Empty);

        // 1. Рішення ухвалює доменний метод.
        Assert.Contains("IsEffectiveOn(", executable, StringComparison.Ordinal);

        // 2. Другого формулювання того самого правила в запиті немає.
        //    Регресія виглядатиме нешкідливо — «оптимізував фільтр у SQL», —
        //    і саме тому її ловить сторож, а не рев'ю.
        Assert.DoesNotContain("a.ValidFrom", executable, StringComparison.Ordinal);
        Assert.DoesNotContain("a.ValidTo", executable, StringComparison.Ordinal);
    }

    /// <summary>Коментарі — не код: у них правило згадується навмисно.</summary>
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
    }
}
