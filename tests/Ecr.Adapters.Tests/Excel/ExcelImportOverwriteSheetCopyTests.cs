// tests/Ecr.Adapters.Tests/Excel/ExcelImportOverwriteSheetCopyTests.cs
using Ecr.Adapters.Excel;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// ✎ B27 (P2, F13 #829): «перезаписати рядок» (AN-114, D-338) зачіпає лише аркуш-копію, рядок якого людина позначила.
/// Та сама таблиця <c>T1</c> з тим самим ключем <c>R1</c> стоїть на «Stacks 1» (екземпляр 729) і «Stacks 2» (730),
/// обидва рядки — конфлікти «змінено після експорту».
/// </summary>
/// <remarks>
/// ⛔ Червоне без фіксу: <see cref="ImportOverwriteRow"/> мав лише таблицю й рядок, і <c>ExcelImporter.Effective</c>
/// брав перезаписувану зміну з УСІХ екземплярів — позначка копії переписувала й оригінал (у плані застосування зміна
/// оригіналу 7 → поверх чужого значення, <c>overwritten</c> = {729, 730}). Мутація «ігнорувати
/// <c>TableInstanceId</c>» (матч лише за таблицею й рядком) — червоне на першому тесті.
/// </remarks>
public sealed class ExcelImportOverwriteSheetCopyTests
{
    private const long DocumentId = 1;
    private const int Period = 202609;
    private const long OriginalInstance = 729;
    private const long CopyInstance = 730;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-338")]
    public void Позначка_рядка_копії_перезаписує_лише_копію_а_оригінал_лишає_чуже_значення()
    {
        var (plan, overwritten) = ExcelImporter.Effective(
            Plan(), [new ImportOverwriteRow("T1", "R1", CopyInstance)]);

        Assert.Equal([CopyInstance], overwritten!.Keys.Order());
        Assert.Empty(Diff(plan, OriginalInstance).Changes);

        var change = Assert.Single(Diff(plan, CopyInstance).Changes);
        Assert.Equal(51m, change.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-338")]
    public void Позначка_оригіналу_не_чіпає_копію()
    {
        var (plan, overwritten) = ExcelImporter.Effective(
            Plan(), [new ImportOverwriteRow("T1", "R1", OriginalInstance)]);

        Assert.Equal([OriginalInstance], overwritten!.Keys.Order());
        Assert.Equal(7m, Assert.Single(Diff(plan, OriginalInstance).Changes).NewValue);
        Assert.Empty(Diff(plan, CopyInstance).Changes);
    }

    /// <summary>
    /// ⚠ Клієнт до B27 екземпляра не шле — колишня поведінка (усі екземпляри з цими таблицею й рядком), щоб
    /// застосування не ламалося посеред оновлення. Клієнт B27 шле екземпляр завжди.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-338")]
    public void Без_екземпляра_колишня_поведінка_усі_екземпляри()
    {
        var (_, overwritten) = ExcelImporter.Effective(Plan(), [new ImportOverwriteRow("T1", "R1")]);

        Assert.Equal([OriginalInstance, CopyInstance], overwritten!.Keys.Order());
    }

    /// <summary>Екземпляр, у якому цей рядок НЕ був конфліктом, — відмова всього застосування, як і чужий рядок.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-338")]
    public void Екземпляр_без_конфлікту_цього_рядка_відхиляється()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => ExcelImporter.Effective(Plan(), [new ImportOverwriteRow("T1", "R1", 999)]));

        Assert.Equal("ECR-IMP-0422", error.ErrorCode);
    }

    private static ImportPlan Plan() => new(
        DocumentId,
        Period,
        [Conflict(OriginalInstance, 7m), Conflict(CopyInstance, 51m)]);

    private static TableDiff Conflict(long instance, decimal value) => new(
        instance,
        Period,
        [],
        [new ImportRejection("R1", "A", "ECR-CELL-0409", "diag", "T1", null, ImportMessageKeys.RowChangedSinceExport)
            { TableInstanceId = instance }],
        new Dictionary<string, string> { ["R1"] = "0xAA" },
        [new ImportChange("R1", "A", 5m, value, "T1") { TableInstanceId = instance }]);

    private static TableDiff Diff(ImportPlan plan, long instance) => Assert.Single(plan.Tables, t => t.TableInstanceId == instance);
}
