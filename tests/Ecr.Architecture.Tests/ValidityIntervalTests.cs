using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Чинність порівнюється **напівінтервально** і рівно одним формулюванням
/// (директива ПК-1 №05 §7, пастка 5, крок <c>I.10</c>).
/// </summary>
/// <remarks>
/// ⛔ Правило перевіряється по ВИХІДНОМУ ТЕКСТУ, і причина та сама, що в
/// <c>RoleValidityRuleTests</c>: закрите порівняння
/// (<c>date &lt;= ValidTo</c>) відрізняється від правильного рівно на день і
/// збігається з ним на будь-яких даних, окрім однієї дати на вікно. Жоден
/// поведінковий тест, написаний «на око», такої регресії не спіймає, а
/// виглядатиме вона нешкідливо — «зробив фільтр прямо в запиті».
///
/// ⚠ Виняток рівно один і він названий поіменно —
/// <c>RoleAssignment.IsEffectiveOn</c>: там межа походить із наказу про
/// підміну («до 31 травня»), а не з міграції, і вона включна свідомо
/// (<c>D2-123</c>). Виняток заданий списком файлів, а не мовчазним пропуском:
/// другий такий випадок має вимагати запису в журнал рішень, а не одного
/// рядка в регулярному виразі.
/// </remarks>
public sealed partial class ValidityIntervalTests
{
    /// <summary>Файли, яким закрите порівняння дозволене — із причиною.</summary>
    private static readonly Dictionary<string, string> Exempt =
        new(StringComparer.Ordinal)
        {
            ["src/Ecr.Domain/Entities/Security/RoleAssignment.cs"] =
                "D2-123: межа з наказу про підміну, а не з міграції; включна свідомо",
        };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Верхня_межа_чинності_ніде_не_порівнюється_включно()
    {
        var offenders = new List<string>();

        foreach (var file in SourceTree.Production())
        {
            if (Exempt.ContainsKey(file.Path))
            {
                continue;
            }

            foreach (var (line, text) in file.CodeLines())
            {
                if (ClosedUpperBound().IsMatch(text))
                {
                    offenders.Add($"{file.Path}:{line} — {text.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Резолвер_констант_питає_домен_а_не_повторює_його_умову()
    {
        var resolver = SourceTree.Production("Ecr.Calculations")
            .Single(f => f.Path.EndsWith("/ConstantResolver.cs", StringComparison.Ordinal));

        var executable = string.Join(
            '\n', resolver.CodeLines().Select(l => l.Text));

        // 1. Рішення ухвалює доменний метод.
        Assert.Contains("IsValidOn(", executable, StringComparison.Ordinal);

        // 2. Другого формулювання того самого правила в резолвері немає.
        //    Саме воно тут і стояло — закритим інтервалом, на день довшим за
        //    модель: у день межі до формули підставився б торішній коефіцієнт
        //    емісії, і жодної відмови при цьому не виникло б.
        Assert.DoesNotContain("c.ValidTo", executable, StringComparison.Ordinal);
        Assert.DoesNotContain("c.ValidFrom", executable, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Міграція_напівінтервала_переносить_дані_а_не_лише_правила()
    {
        // ⛔ Найдорожча частина кроку `I.10` — не код, а ці два `UPDATE`.
        // Без них зміна сенсу `ValidTo` вкоротила б КОЖЕН наявний рядок на
        // добу: дозвіл, чинний до 31 грудня, перестав би діяти 31 грудня, а
        // побачив би це лише той, хто саме того дня заповнює звіт. Схема при
        // цьому лишається бездоганною, і жоден інший тест такої втрати не
        // помічає.
        var path = Path.Combine(
            SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Migrations",
            "20260907045308_I10HalfOpenValidity.cs");

        // ⚠ Коментарі відкидаються, а рядкові літерали — ні: сам SQL живе
        // саме в літералах, а `sec.RoleAssignment` згадується в коментарі
        // навмисно — щоб пояснити, чому його тут немає.
        var migration = string.Join(
            '\n',
            File.ReadLines(path)
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Contains(
            "UPDATE dic.RegistryEntry SET ValidTo = CAST(DATEADD(day, 1, ValidTo) AS date)",
            migration,
            StringComparison.Ordinal);

        Assert.Contains(
            "UPDATE calc.MethodologyConstant SET ValidTo = CAST(DATEADD(day, 1, ValidTo) AS date)",
            migration,
            StringComparison.Ordinal);

        // ⚠ І симетрично: `sec.RoleAssignment` міграція чіпати НЕ має. Там
        // межа лишається включною (`D2-123`), і зсув на добу відібрав би
        // права на день раніше, ніж написано в наказі про підміну.
        Assert.DoesNotContain("sec.RoleAssignment", migration, StringComparison.Ordinal);
    }

    /// <summary>
    /// Закрите порівняння верхньої межі: <c>date &lt;= ValidTo</c>,
    /// <c>ValidTo &gt;= date</c> у будь-якому написанні.
    /// </summary>
    [GeneratedRegex(
        @"(<=\s*[\w\.]*Valid(To|Until)\b)|(\bValid(To|Until)\w*\s*>=)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClosedUpperBound();
}
