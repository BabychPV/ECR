// tests/Ecr.Infrastructure.Tests/Jobs/PartitionCheckJobTests.cs
using System.Text.RegularExpressions;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Перевірка запасу партицій. Задача **алертить**, а `SPLIT` робить SQL Agent
/// (D-66): обліковий запис застосунку не має DDL-прав у PROD.
/// </summary>
public sealed partial class PartitionCheckJobTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Нестача_запасу_партицій_дає_попередження()
    {
        // Одна межа попереду проти мінімуму у дві — нестача.
        Assert.True(1 < PartitionCheckJob.MinimumBoundariesAhead);

        // ⚠ Мінімум саме два, і це не «про запас»: одна межа витрачається на
        // поточний місяць, друга лишається на час, поки хтось прочитає алерт.
        // З однією межею SPLIT доводиться робити вже по непорожній партиції —
        // а це переміщення даних із блокуванням, не операція метаданих.
        Assert.Equal(2, PartitionCheckJob.MinimumBoundariesAhead);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Задача_не_виконує_DDL()
    {
        var source = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Jobs", "PartitionCheckJob.cs"));

        // ⛔ Жодного DDL у коді задачі. `SPLIT` робить SQL Agent під окремим
        // principal (D-66): обліковий запис застосунку DDL-прав у PROD не має
        // і мати не повинен. Право створювати партиції — це право створювати
        // будь-що, і задача, яка ним володіє, перестає бути безпечною.
        //
        // Перевірка читає ВИХІДНИЙ КОД: рефлексія побачила б метод і не
        // побачила, що всередині `ALTER PARTITION FUNCTION`.
        var executable = Comments().Replace(source, string.Empty);

        Assert.DoesNotContain("ALTER PARTITION", executable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SPLIT RANGE", executable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CREATE TABLE", executable, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usp_EnsurePartitions", executable, StringComparison.Ordinal);

        // А сам SPLIT існує — у скрипті, який виконує SQL Agent.
        var script = File.ReadAllText(Path.Combine(
            SolutionRoot(), "src", "Ecr.Infrastructure", "Persistence", "Sql",
            "04-partition-maintenance.sql"));

        Assert.Contains("SPLIT RANGE", script, StringComparison.Ordinal);
    }

    // ⛔ Q-185: `Достатній_запас_не_породжує_шуму` робив лише
    // `Assert.Contains` на текст файлу — доведено мутацією (інверсія умови
    // лишала перевірений підрядок незмінним). Перенесено в
    // `PartitionCheckJobDetectionTests` — реальний запуск job-и проти
    // справжніх меж `pf_ByPeriodKey` на SQLEXPRESS, з обома гілками
    // (Succeeded/Degraded) через симульований годинник.

    /// <summary>Коментарі коду — те, що не виконується.</summary>
    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>Корінь репозиторію.</summary>
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
