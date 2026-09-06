using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Гейт <c>BR-07</c> має лишатися ЗАПУСКНИМ і мати право падати.
/// </summary>
/// <remarks>
/// ⛔ Це сторож на дефект, який уже стався і не давав жодного сигналу.
/// <c>GateBenchmark</c> був написаний на Етапі 1, описаний у
/// <c>05j-skeleton-tools.md</c>, зарахований у <c>progress.md</c> рядком
/// «5 із 6 замірів гейта» — і не викликався **нізвідки**. Компілятор мовчав
/// (публічний клас без викликів — не помилка), тести мовчали, документація
/// стверджувала протилежне. Рішення про фізичну модель зберігання
/// (<c>D-21</c>) чекало на числа, зняти які не могла жодна команда.
///
/// ⚠ Перевіряється саме ТЕКСТ, а не типи: «клас, на який ніхто не
/// посилається» в IL невідрізнимий від «класу, на який посилаються через
/// рефлексію». А втратити тут можна не клас, а сполучну ланку — виклик і
/// код виходу.
/// </remarks>
public sealed class Br07GateReachableTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "BR-07")]
    public void Гейт_BR07_має_точку_входу_і_ненульовий_код_виходу()
    {
        var program = ReadTool("Ecr.DataGen", "Program.cs");

        Assert.Contains("new GateBenchmark", program, StringComparison.Ordinal);

        // ⛔ Ненульовий код виходу — єдине, що відрізняє перевірку від звіту.
        // Прогін, який друкує числа і завжди виходить нулем, конвеєр пропустить
        // разом із порушеним бюджетом: рівно так `D-132` і не перевірявся,
        // поки `npm run build` вважали гейтом (`07-checkpoints.md`).
        Assert.Contains("result.Passed ? 0 : 2", program, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "BR-07")]
    public void Генератор_цілить_у_doc_CellValue_а_не_в_результати()
    {
        // ⛔ Обсяг системи — це `doc.CellValue` (~108 млн рядків на рік),
        // а `calc.CalculationResult` — ~4 млн, у двадцять сім разів менше.
        // Навантажувальна перевірка, яка наповнює таблицю результатів, дала б
        // зелений бюджет і не означала б нічого; застереження прямо записане
        // в директиві №06 по кроку II.10.
        var loader = ReadSource("Ecr.Infrastructure", "Persistence", "BulkCellLoader.cs");

        Assert.Contains("DestinationTableName = \"doc.CellValue\"", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("calc.CalculationResult", loader, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "BR-07")]
    public void Скрипт_гейта_не_видаляє_чужих_баз()
    {
        // ⛔ Скрипт створює тимчасову базу на машині, де живуть бази замовника
        // (`EcrDev`, `EcrTest*`). Ім'я приходить параметром, тому `DROP` без
        // звірки власної позначки — помилка, яку не можна зробити один раз.
        var script = File.ReadAllText(Path.Combine(SolutionRoot(), "tools", "br07-load-test.ps1"));

        Assert.Contains("Ecr_Br07_Temp", script, StringComparison.Ordinal);

        // ⚠ Позначка малих файлів — окрема вимога: без неї `01-filegroups.sql`
        // дає 4096+4096+4096+2048 МБ на порожню базу, і сімнадцять таких баз
        // уже з'їли 152 ГБ (`09-commands.md` §3).
        Assert.Contains("Ecr_SmallFiles", script, StringComparison.Ordinal);
    }

    private static string ReadTool(string project, params string[] parts)
        => File.ReadAllText(Path.Combine([SolutionRoot(), "tools", project, .. parts]));

    private static string ReadSource(string project, params string[] parts)
        => File.ReadAllText(Path.Combine([SolutionRoot(), "src", project, .. parts]));

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
