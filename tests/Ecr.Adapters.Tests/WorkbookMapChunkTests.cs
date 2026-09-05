// tests/Ecr.Adapters.Tests/WorkbookMapChunkTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Карта книги не влазить в одну комірку і пишеться шматками.
/// </summary>
/// <remarks>
/// ⛔ `A7-29`. Excel не приймає в комірку більше за 32 767 символів, а карта
/// реального документа — сотні кілобайт: дев'яносто таблиць по сорок колонок.
/// Експорт падав на КОЖНОМУ несинтетичному документі, причому вже після того,
/// як уся книга була побудована: користувач бачив задачу, що дійшла до 10 % і
/// померла з повідомленням про максимум символів.
///
/// ⚠ Тестові книги завжди були маленькі — саме тому дефект дожив до запуску на
/// реальному обсязі.
/// </remarks>
public sealed class WorkbookMapChunkTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Межа_шматка_менша_за_стелю_Excel()
    {
        // 32 767 — це стеля формату, а не робоче значення: писати рівно в неї
        // означало б покладатися на те, що жоден символ не займе більше.
        Assert.True(ExcelWorkbookMap.ChunkSize < 32_767);
        Assert.True(ExcelWorkbookMap.ChunkSize > 1_000);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-4.3")]
    public void Довгий_текст_розкладений_по_рядках_збирається_назад()
    {
        // Модель того, що робить експортер і читає імпортер: без цього
        // перевірка залежала б від побудови цілої книги на 90 таблиць.
        var json = new string('x', (ExcelWorkbookMap.ChunkSize * 2) + 17);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(ExcelWorkbookMap.SheetName);

        for (var offset = 0; offset < json.Length; offset += ExcelWorkbookMap.ChunkSize)
        {
            var length = Math.Min(ExcelWorkbookMap.ChunkSize, json.Length - offset);

            sheet.Cell((offset / ExcelWorkbookMap.ChunkSize) + 1, 1).Value =
                json.Substring(offset, length);
        }

        var restored = string.Concat(
            sheet.Column(1)
                .CellsUsed()
                .OrderBy(c => c.Address.RowNumber)
                .Select(c => c.GetString()));

        Assert.Equal(json, restored);
        Assert.Equal(3, sheet.Column(1).CellsUsed().Count());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Книга_зі_старою_картою_в_одній_комірці_читається_так_само()
    {
        // ⚠ Сумісність назад: книги, вивантажені до `A7-29`, мають рівно один
        // шматок, і той самий код збирає їх без окремої гілки.
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(ExcelWorkbookMap.SheetName);
        sheet.Cell(1, 1).Value = "{\"sheets\":[]}";

        var restored = string.Concat(
            sheet.Column(1)
                .CellsUsed()
                .OrderBy(c => c.Address.RowNumber)
                .Select(c => c.GetString()));

        Assert.Equal("{\"sheets\":[]}", restored);
    }
}
