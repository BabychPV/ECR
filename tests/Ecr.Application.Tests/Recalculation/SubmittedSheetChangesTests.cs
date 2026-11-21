// tests/Ecr.Application.Tests/Recalculation/SubmittedSheetChangesTests.cs
using Ecr.Application.Recalculation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// ✎ B79: збирач «у поданому аркуші змінилося число» — спільна частина всіх шляхів перерахунку (і B80).
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЇ (локально): (1) у <c>Contains</c> не зводити <c>null</c>-копію до <c>0</c> (порівнювати з іншим ключем) —
/// «Оригінал_і_копія…» червоніє; (2) прибрати з ключа період — «Ключ_розрізняє…» червоніє; (3) прибрати з ключа
/// документ — те саме; (4) <c>AddRange</c> додає лише перший аркуш — «AddRange_додає_всі…» червоніє.
/// </remarks>
public sealed class SubmittedSheetChangesTests
{
    private const long DocumentId = 71;
    private const int Period = 209903;
    private const int SheetDefId = 5;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Порожній_набір_нічого_не_позначає()
    {
        var changes = new SubmittedSheetChanges();

        Assert.Equal(0, changes.Count);
        Assert.False(changes.Contains(DocumentId, Period, SheetDefId, sheetCopyId: null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Ключ_розрізняє_документ_період_і_аркуш()
    {
        var changes = new SubmittedSheetChanges();
        changes.Add(DocumentId, Period, SheetDefId, sheetCopyId: null);

        Assert.True(changes.Contains(DocumentId, Period, SheetDefId, sheetCopyId: null));
        Assert.False(changes.Contains(DocumentId + 1, Period, SheetDefId, sheetCopyId: null));
        Assert.False(changes.Contains(DocumentId, Period + 1, SheetDefId, sheetCopyId: null));
        Assert.False(changes.Contains(DocumentId, Period, SheetDefId + 1, sheetCopyId: null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Оригінал_і_копія_аркуша_окремі_а_повторне_додавання_не_множить()
    {
        var changes = new SubmittedSheetChanges();
        changes.Add(DocumentId, Period, SheetDefId, sheetCopyId: 9);
        changes.Add(DocumentId, Period, SheetDefId, sheetCopyId: 9);

        Assert.Equal(1, changes.Count);
        Assert.True(changes.Contains(DocumentId, Period, SheetDefId, sheetCopyId: 9));
        Assert.False(changes.Contains(DocumentId, Period, SheetDefId, sheetCopyId: null));
        Assert.False(changes.Contains(DocumentId, Period, SheetDefId, sheetCopyId: 10));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void PeriodsOf_дає_періоди_змін_лише_свого_документа_без_повторів_за_зростанням()
    {
        var changes = new SubmittedSheetChanges();
        changes.Add(DocumentId, Period + 2, SheetDefId, sheetCopyId: null);
        changes.Add(DocumentId, Period, SheetDefId, sheetCopyId: null);
        changes.Add(DocumentId, Period, SheetDefId + 1, sheetCopyId: null);
        changes.Add(DocumentId + 1, Period + 5, SheetDefId, sheetCopyId: null);

        Assert.Equal(new[] { Period, Period + 2 }, changes.PeriodsOf(DocumentId));
        Assert.Empty(changes.PeriodsOf(DocumentId + 2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void AddRange_додає_всі_аркуші_шляху_запису_під_документом_і_періодом()
    {
        var changes = new SubmittedSheetChanges();
        changes.AddRange(DocumentId, Period, new (int SheetDefId, long? CopyId)[] { (SheetDefId, null), (SheetDefId + 1, 4) });

        Assert.Equal(2, changes.Count);
        Assert.True(changes.Contains(DocumentId, Period, SheetDefId, sheetCopyId: null));
        Assert.True(changes.Contains(DocumentId, Period, SheetDefId + 1, sheetCopyId: 4));
        Assert.False(changes.Contains(DocumentId, Period, SheetDefId + 1, sheetCopyId: null));
    }

    /// <remarks>
    /// Позначка перерваного прогону (<c>RecalculationJob.FlushSubmittedMarksAsync</c>) пише записи за <c>Items</c>, а
    /// зміни результатів потрапляють у набір прогону лише злиттям після коміту (<c>UnionWith</c>). Мутації: у <c>Items</c>
    /// не повертати копію з <c>0</c> у <c>null</c> — оригінал отримав би кореляцію «копії 0»; <c>UnionWith</c> без злиття
    /// — набір прогону порожній.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B79")]
    public void Items_повертає_оригінал_з_null_копією_а_UnionWith_зливає_без_повторів()
    {
        var run = new SubmittedSheetChanges();
        run.Add(DocumentId, Period, SheetDefId, sheetCopyId: 9);

        var committed = new SubmittedSheetChanges();
        committed.Add(DocumentId, Period, SheetDefId, sheetCopyId: null);
        committed.Add(DocumentId, Period, SheetDefId, sheetCopyId: 9);
        run.UnionWith(committed);

        Assert.Equal(
            new (long, int, int, long?)[] { (DocumentId, Period, SheetDefId, null), (DocumentId, Period, SheetDefId, 9) },
            run.Items.ToArray());
    }
}
