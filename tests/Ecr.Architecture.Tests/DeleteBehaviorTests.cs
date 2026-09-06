using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Конфігурація не оголошує поведінки видалення, якої не буде (<c>ФВ-7.6</c>).
/// </summary>
/// <remarks>
/// ⛔ <c>EcrDbContext.OnModelCreating</c> після всіх конфігурацій проходить
/// **усі** зовнішні ключі моделі й ставить кожному <c>Restrict</c>: у системі
/// діє м'яке видалення, і каскад означав би тихе зникнення історії разом із
/// довідником. Отже будь-який <c>.OnDelete(...)</c> у конфігурації або збігається
/// з цим правилом, або **не діє**.
///
/// ⚠ Не діє — гірше, ніж помилка. <c>MethodologyTestCaseConfiguration</c>
/// оголошувала <c>Cascade</c> з коментарем «каскад навмисний», і той, хто це
/// читав, мав усі підстави вважати, що тести зникають разом із версією. Ні
/// компілятор, ні тест, ні міграція цього не спростовували: поведінка в базі
/// весь час була <c>Restrict</c>, а джерело весь час казало інше.
///
/// ⛔ Перевірка йде по ДЖЕРЕЛУ, а не по побудованій моделі, і це навмисно.
/// Модель питати марно: цикл у <c>OnModelCreating</c> вирівнює її під
/// <c>Restrict</c> завжди, тож така перевірка була б зелена незалежно від
/// того, що написано в конфігураціях, — тобто зелена з хибної причини.
/// </remarks>
public sealed partial class DeleteBehaviorTests
{
    /// <summary>Оголошення поведінки видалення в конфігурації.</summary>
    [GeneratedRegex(@"\.OnDelete\(DeleteBehavior\.(\w+)\)")]
    private static partial Regex DeleteBehaviorCall();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Конфігурації_не_оголошують_поведінки_видалення_крім_Restrict()
    {
        var directory = Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Configurations");

        Assert.True(Directory.Exists(directory), $"Немає каталогу конфігурацій: {directory}");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match match in DeleteBehaviorCall().Matches(File.ReadAllText(file)))
            {
                var behavior = match.Groups[1].Value;

                if (!string.Equals(behavior, "Restrict", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: DeleteBehavior.{behavior}");
                }
            }
        }

        // ⛔ Повідомлення несе ФАЙЛ і ПОВЕДІНКУ: «десь оголошено каскад» не
        // сказало б, чи це недогляд, чи свідома спроба обійти м'яке видалення,
        // — а лікуються вони по-різному.
        Assert.Empty(offenders.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Цикл_який_вирівнює_поведінку_на_місці()
    {
        // ⚠ Сторож вище має сенс лише доти, доки цикл у `OnModelCreating`
        // справді все вирівнює. Прибрати цикл і лишити перевірку джерела
        // означало б стерегти правило, якого більше немає: конфігурації
        // мовчали б про каскад, а модель його дозволяла б.
        var context = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "EcrDbContext.cs"));

        Assert.Contains("fk.DeleteBehavior = DeleteBehavior.Restrict;", context, StringComparison.Ordinal);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
