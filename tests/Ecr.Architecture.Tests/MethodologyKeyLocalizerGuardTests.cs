using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// C1: читання правил (<c>MatchJson</c>) і обов'язкових входів методології для зіставлення з комірками
/// документа іде лише через локалізатор ключів.
/// </summary>
/// <remarks>
/// ⛔ Id колонок у правилах — колонки тієї версії шаблону, у якій їх писали. Документ на клон-версії має інші Id
/// тих самих колонок; споживач, що пропустив переклад, дає ТИХИЙ дефект (правило не збігається ні з чим, а
/// <c>Block</c>-вимога блокує збереження назавжди). Сторож не доводить правильності перекладу — це роблять тести
/// споживачів, — він лише не дає додати ще одне місце читання без нього.
/// </remarks>
public sealed class MethodologyKeyLocalizerGuardTests
{
    private static readonly string[] Tokens =
    [
        ".GetRulesAsync(", ".GetRequiredInputsAsync(", ".MethodologyRules", ".MethodologyRequiredInputs",
    ];

    /// <summary>Файли, яким переклад не потрібен, з причиною.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["src/Ecr.Application/Calculations/PublishMethodologyHandler.cs"] =
            "перевірки публікації: структура предикатів і перетин правил між собою, комірки документів не читаються",
        ["src/Ecr.Infrastructure/Persistence/CalculationBindingStore.cs"] =
            "лише ЧИ Є активні правила в опублікованій версії (перевірка конфлікту прив'язок на публікації шаблону); умови не читаються",
        ["src/Ecr.Infrastructure/Persistence/MethodologyDraftStore.cs"] =
            "редагування й клон ЧЕРНЕТКИ методології: ключі лишаються як у джерелі (Id у сховищі - навмисно)",
        ["src/Ecr.Infrastructure/Persistence/MethodologyVersionDeletionStore.cs"] =
            "видалення версії методології",
        ["src/Ecr.Infrastructure/Persistence/MethodologyStore.cs"] =
            "сховище, що віддає правила як є; переклад робить споживач",
        ["src/Ecr.Infrastructure/Persistence/EcrDbContext.cs"] = "оголошення DbSet",
        ["src/Ecr.Infrastructure/Persistence/WhereUsedStore.cs"] =
            "«де використовується» шукає за СІМ'ЄЮ колонки (відповідники за шляхом у версіях шаблону) - ColumnFamilyAsync",
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Читання_правил_і_вимог_методології_іде_через_локалізатор_ключів()
    {
        var offenders = new List<string>();

        foreach (var file in SourceTree.Production())
        {
            if (file.Path.Contains("/Ports/", StringComparison.Ordinal) || Exempt.ContainsKey(file.Path))
            {
                continue;
            }

            var reads = file.CodeLines()
                .Where(l => Tokens.Any(t => l.Text.Contains(t, StringComparison.Ordinal)))
                .Select(l => l.Line)
                .ToList();
            if (reads.Count == 0)
            {
                continue;
            }

            var localized = file.Text.Contains("MethodologyKeyLocalizer", StringComparison.Ordinal)
                            || file.Text.Contains("IColumnPathMapper", StringComparison.Ordinal)
                            || file.Text.Contains("ColumnPathMapper", StringComparison.Ordinal);
            if (!localized)
            {
                offenders.Add($"{file.Path}: рядки {string.Join(", ", reads)}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Читання правил/обов'язкових входів методології без MethodologyKeyLocalizer (C1) - документ на клон-версії "
            + "шаблону не збігатиметься з правилами:\n" + string.Join("\n", offenders));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Винятки_сторожа_існують_у_дереві()
    {
        // Виняток для файла, якого вже немає, - мертвий дозвіл, що колись прикриє нове місце читання.
        var existing = SourceTree.Production().Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        Assert.All(Exempt.Keys, k => Assert.Contains(k, existing));
    }
}
