using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// UI-тест 20.11: заголовок панелі «Проблеми розрахунку» стверджував «подання заблоковано», а кнопка «Подати» лишалась
/// активною. Панель рахує помилки формул ДОКУМЕНТА за період, що відкрито, із збереженого прогону, а сервер на подання
/// перераховує ЛИШЕ поданий аркуш (і подається може інший період) — тож безумовне «заблоковано» неправдиве.
/// Формулювання не стверджує блокування у жодній мові.
/// </summary>
public sealed class CalcIssuesTitleWordingTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("en", "submit is blocked")]
    [InlineData("ru", "подача заблокирована")]
    [InlineData("kz", "тапсыру бұғатталған")]
    public void Заголовок_панелі_не_стверджує_що_подання_заблоковано(string lang, string forbidden)
    {
        var path = Path.Combine(SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql", "seed", "features", "D475-formula-errors.sql");
        // ✎ W15y-T1: рядок каталогу — три рядкові поля (ключ, мова, текст); рядок «Змінені тексти» (#UiChanged) — чотири
        // (ключ, мова, старе, нове), і старе там навмисно «заблоковано».
        var line = File.ReadAllLines(path)
            .Single(l => l.Contains("document.calcIssues.titleBlocked", StringComparison.Ordinal)
                         && l.Contains($"N'{lang}'", StringComparison.Ordinal)
                         && l.Split("N'").Length - 1 == 3);

        Assert.DoesNotContain(forbidden, line, StringComparison.Ordinal);
        Assert.Contains("{period}", line, StringComparison.Ordinal);
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
