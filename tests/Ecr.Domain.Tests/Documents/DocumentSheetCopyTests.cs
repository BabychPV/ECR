// tests/Ecr.Domain.Tests/Documents/DocumentSheetCopyTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): джерело копії — завжди оригінал; оригінал має номер 1, копії — 2…;
/// назву групи міняють на оригіналі; перенумеровують лише копії.
/// </summary>
public sealed class DocumentSheetCopyTests
{
    private static DocumentSheet SavedOriginal(long id = 41)
    {
        var sheet = new DocumentSheet(documentId: 7, sheetDefId: 3);
        typeof(DocumentSheet).BaseType!.GetProperty("Id")!.SetValue(sheet, id);
        return sheet;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "RS-1103")]
    public void Оригінал_номер_1_копія_посилається_на_оригінал()
    {
        var original = SavedOriginal();

        var copy = original.CreateCopy(2);

        Assert.False(original.IsCopy);
        Assert.Equal(DocumentSheet.OriginalOrdinal, original.CopyOrdinal);
        Assert.True(copy.IsCopy);
        Assert.Equal(41, copy.CopyOfId);
        Assert.Equal(2, copy.CopyOrdinal);
        Assert.Equal((7L, 3), (copy.DocumentId, copy.SheetDefId));
        Assert.True(copy.IsIncluded);
        Assert.Null(copy.NameOverrideL10n);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "RS-1103")]
    public void Копія_копії_і_номер_менше_2_відхиляються()
    {
        var original = SavedOriginal();
        var copy = original.CreateCopy(2);

        Assert.Throws<InvalidOperationException>(() => copy.CreateCopy(3));
        Assert.Throws<InvalidOperationException>(() => original.CreateCopy(1));
        Assert.Throws<InvalidOperationException>(() => new DocumentSheet(7, 3).CreateCopy(2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "RS-1103")]
    public void Назву_міняють_на_оригіналі_номер_на_копії()
    {
        var original = SavedOriginal();
        var copy = original.CreateCopy(3);
        var name = new LocalizedText(new Dictionary<string, string> { ["en"] = "Boiler" });

        original.Rename(name);
        copy.Renumber(2);

        Assert.Equal(name.ToJson(), original.NameOverrideL10n!.ToJson());
        Assert.Equal(2, copy.CopyOrdinal);
        Assert.Throws<InvalidOperationException>(() => copy.Rename(name));
        Assert.Throws<InvalidOperationException>(() => original.Renumber(2));
        Assert.Throws<InvalidOperationException>(() => copy.Renumber(1));
    }
}
