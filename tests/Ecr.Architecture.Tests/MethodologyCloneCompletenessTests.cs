// tests/Ecr.Architecture.Tests/MethodologyCloneCompletenessTests.cs
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Клон версії методології переносить **кожен** набір дочірніх записів.
/// </summary>
/// <remarks>
/// ⛔ Клонування — єдиний спосіб змінити опубліковану версію (ФВ-9.1), і
/// забутий набір не має симптому. Клон без констант рахує тими самими виразами
/// по порожніх коефіцієнтах — і публікація його не спинить: зелений тест
/// звіряє РЕЗУЛЬТАТ, а результат порахується, просто інший. Клон без імпортів
/// мовчки втрачає доступ до чужих формул; клон без тестів неможливо
/// опублікувати взагалі (ФВ-9.12).
///
/// ⚠ Сторож дивиться на СУТНОСТІ, а не на перелік, переписаний руками: він
/// бере всі типи домену, що належать версії методології, і питає, чи згадує їх
/// копіювальник. Сутність, додана завтра, зробить його червоним — а перелік у
/// документі мовчав би.
/// </remarks>
public sealed class MethodologyCloneCompletenessTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Клон_версії_методології_переносить_кожен_набір_дочірніх_записів()
    {
        var cloner = SourceTree
            .Production("Ecr.Infrastructure")
            .SingleOrDefault(f => f.Path.EndsWith("MethodologyDraftStore.cs", StringComparison.Ordinal));

        Assert.NotNull(cloner);

        var children = Children();
        Assert.NotEmpty(children);

        var forgotten = children
            .Where(type => !Excluded.Contains(type))
            .Where(type => !Mentioned(cloner.Text, type))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            forgotten.Count == 0,
            "Набори дочірніх записів версії методології, яких клон не переносить:"
            + Environment.NewLine + string.Join(Environment.NewLine, forgotten));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_рівня_2_не_створює_ніщо_а_отже_клонові_нема_чого_переносити()
    {
        // ⛔ Це не окрема перевірка, а **термін придатності** звільнення вище.
        // `ScriptVersion` належить версії методології, і клон його не копіює —
        // законно рівно доти, доки рівень 2 не будується (ФВ-9.3, `D-105`) і
        // рядків цієї таблиці не існує в принципі.
        //
        // ⚠ Тому звільнення прив'язане до факту, а не до наміру: щойн хтось
        // напише `new ScriptVersion(`, цей тест почервоніє і змусить довчити
        // клон. Інакше день, коли рівень 2 ухвалять, став би днем, коли клони
        // почали мовчки губити код методології — а ФВ-9.1 обіцяє, що клон є
        // повною копією.
        var constructed = SourceTree
            .Production()
            .Where(file => file.CodeLines().Any(line => line.Text.Contains(
                $"new {nameof(ScriptVersion)}(", StringComparison.Ordinal)))
            .Select(file => file.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            constructed.Count == 0,
            $"{nameof(ScriptVersion)} тепер створюється — клон версії методології "
            + "зобов'язаний його переносити:"
            + Environment.NewLine + string.Join(Environment.NewLine, constructed));
    }

    /// <summary>
    /// Набори, які клон не переносить **навмисно**.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>CalculationResult</c> — не вміст версії, а її НАСЛІДОК. Скопіювати
    /// результати в нову версію означало б завести числа, яких ніхто не
    /// рахував, із посиланням на версію, що ще нічого не виконувала (ФВ-9.11):
    /// питання «яким прогоном пораховано цей рядок» дістало б відповідь, якої
    /// не було.
    ///
    /// ⚠ <c>ScriptVersion</c> — рівень 2, який не будується (ФВ-9.3,
    /// <c>D-105</c>). Звільнення діє, доки цю сутність не створює ніщо; за цим
    /// стежить сусідній тест.
    /// </remarks>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        nameof(CalculationResult),
        nameof(ScriptVersion),
    };

    /// <summary>Типи домену, що належать версії методології.</summary>
    /// <returns>Імена сутностей із полем <c>MethodologyVersionId</c>.</returns>
    /// <remarks>
    /// ⚠ Ознака — саме <c>MethodologyVersionId</c>. <c>MethodologyDependency</c>
    /// сюди не потрапляє за побудовою, і правильно: ребро графа належить
    /// МЕТОДОЛОГІЇ, а не версії, і перебудовує його публікація за фактичними
    /// посиланнями виразів.
    /// </remarks>
    private static IReadOnlyList<string> Children()
        => [.. typeof(MethodologyVersion).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => string.Equals(
                t.Namespace, typeof(MethodologyVersion).Namespace, StringComparison.Ordinal))
            .Where(t => t.GetProperty("MethodologyVersionId") is not null)
            .Select(t => t.Name)];

    /// <summary>Чи згадує копіювальник цю сутність поза коментарями.</summary>
    /// <param name="source">Текст файлу сховища.</param>
    /// <param name="type">Ім'я сутності.</param>
    /// <returns><c>true</c>, якщо ім'я є в коді.</returns>
    /// <remarks>
    /// ⛔ Коментарі відрізаються. Інакше сторож зарахував би пояснення «а ось
    /// цього ми поки не переносимо» за перенесення — тобто був би зеленим саме
    /// на тому дефекті, заради якого написаний.
    /// </remarks>
    private static bool Mentioned(string source, string type)
        => SourceTree
            .CodeLines(new SourceFile("MethodologyDraftStore.cs", source))
            .Any(line => Regex.IsMatch(
                line.Text,
                $@"\b{Regex.Escape(type)}\b",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)));
}
