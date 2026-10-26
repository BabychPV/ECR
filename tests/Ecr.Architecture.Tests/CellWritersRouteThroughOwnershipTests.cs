using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Q-17.5: ніхто, крім наперед відомих записувачів, не пише комірки напряму через <c>ICellStore.ApplyAsync</c>/<c>ApplyBatchAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// Чому це сторож маршрутизації власників. Запис у стовпець з іншим власником (місячний m / не-місячний → якір,
/// <c>Collection:MonthColumnRouting</c>) робить <c>PatchCellsHandler</c> (<c>RouteForeignOwnersAsync</c>). Інтеграційні
/// писачі — <c>IntegrationCellPatcher</c> і <c>MaterializeCollectedDataJob</c> — НЕ мають власного шляху в комірки (D-118):
/// вони готують запит і віддають його тому самому <c>PatchCellsHandler</c>, тож їхній запис у колонку з чужим власником
/// маршрутизується тим самим роутером. Другий, прямий шлях у <c>ICellStore.Apply*</c> обійшов би власників — тому цей
/// перелік фіксований: новий запис у файл поза переліком червоніє й вимагає рішення (маршрутизувати чи обґрунтувати).
/// </para>
/// <para>
/// ⚠ Перевірка SQL на момент заведення (26.10, лише читання): у <c>EcrLandUi</c>, <c>EcrLandUi_PermitsTry</c>,
/// <c>EcrDev</c>, <c>EcrContractL2</c> мапінгів збору немає взагалі (<c>ext.EntityFieldMap</c> = 0 рядків,
/// <c>ext.SourceEventFieldMap</c> = 0), тож мапінгів у <c>IsMonthColumn = 1</c> чи введені не-місячні колонки таблиць
/// <c>MonthsInColumns</c> (у Land їх 675 таблиць, 16 236 місячних колонок) наразі немає. Якщо з'являться — запис піде
/// через роутер; зі звичайним (прапор вимкнено) шляхом поведінка не змінюється.
/// </para>
/// <para>
/// Дозволені прямі писачі: <c>PatchCellsHandler*</c> (роутер), <c>MonthRoutedCellStore</c> (прозорий декоратор),
/// <c>RecalculationService</c> (обчислені комірки — власник завжди екземпляр перегляду), <c>PermitBindingService</c>
/// (рядки з дозволу: динамічні таблиці прив'язки).
/// </para>
/// </remarks>
public sealed class CellWritersRouteThroughOwnershipTests
{
    private static readonly string[] AllowedDirectWriters =
    [
        "MonthRoutedCellStore.cs",
        "PatchCellsHandler.Workbook.cs",
        "PatchCellsHandler.cs",
        "PermitBindingService.cs",
        "RecalculationService.cs",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Requirement", "Q-17")]
    public void Прямий_запис_у_комірки_через_ICellStore_є_лише_в_відомих_писачів()
    {
        var writers = SourceTree.Production()
            .Where(file => Regex.IsMatch(
                file.Text, @"\b(cellStore|cells|inner)\.Apply(Batch)?Async\(", RegexOptions.None, TimeSpan.FromSeconds(5)))
            .Select(file => Path.GetFileName(file.Path))
            .Order(StringComparer.Ordinal)
            .ToList();

        // ⛔ Самоперевірка: порожній перелік означав би зламаний пошук, а не відсутність писачів.
        Assert.NotEmpty(writers);

        Assert.True(
            writers.SequenceEqual(AllowedDirectWriters),
            "Прямий запис у комірки (ICellStore.ApplyAsync/ApplyBatchAsync) знайдено у файлах: "
            + string.Join(", ", writers) + ". Очікувалося: " + string.Join(", ", AllowedDirectWriters)
            + ". Новий писач має йти через PatchCellsHandler (роутер власників Q-17) або бути названий тут із причиною.");
    }
}
