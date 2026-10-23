using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// D-443 (AN-130): учасник документа — рівень і область на <see cref="EditAssignment"/>, передача власника
/// документа. Рівень Write — колишня передача місяця без змін (ключ зрізу той самий).
/// </summary>
/// <remarks>
/// Мутації: прибрати суфікс <c>|L=</c> у <c>EditAssignment.KeyOf</c> — червоніє «ключ_рівня»; прибрати
/// перевірку місяця/періоду/ексклюзивності в <c>RequireLevelFits</c> — червоніє «місяць_лише_для_Write»;
/// дозволити <c>Manage</c> у <c>IsAssignableLevel</c> — червоніє «Manage_не_видається»; прибрати відмову
/// «той самий власник» у <c>Document.TransferOwnership</c> — червоніє «передача_тому_самому».
/// </remarks>
public sealed class DocumentParticipantDomainTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

    private static EditAssignment Create(EditAssignmentSlice slice, GrantLevel level, bool exclusive = false)
        => new(7, slice, exclusive, 10, Guid.NewGuid(), 10, T0, level: level);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData(GrantLevel.Write, "S=F1|T=T1|M=*|P=*")]
    [InlineData(GrantLevel.Read, "S=F1|T=T1|M=*|P=*|L=Read")]
    [InlineData(GrantLevel.Submit, "S=F1|T=T1|M=*|P=*|L=Submit")]
    [InlineData(GrantLevel.Approve, "S=F1|T=T1|M=*|P=*|L=Approve")]
    public void Ключ_рівня_Write_незмінний_інші_з_суфіксом(GrantLevel level, string expected)
    {
        var assignment = Create(EditAssignmentSlice.Create("F1", "T1", null, null), level);

        Assert.Equal(expected, assignment.SliceKey);
        Assert.Equal(level, assignment.Level);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void За_замовчуванням_рівень_Write_як_у_передачі_місяця()
    {
        var assignment = new EditAssignment(
            7, EditAssignmentSlice.Create("F1", null, 3, null), isExclusive: true, 10, Guid.NewGuid(), 10, T0);

        Assert.Equal(GrantLevel.Write, assignment.Level);
        Assert.Equal("S=F1|T=*|M=3|P=*", assignment.SliceKey);
        Assert.True(assignment.IsExclusive);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData(null, null, EditAssignmentScope.Document)]
    [InlineData("F1", null, EditAssignmentScope.Sheet)]
    [InlineData("F1", "T1", EditAssignmentScope.Table)]
    public void Область_похідна_від_селекторів(string? sheet, string? table, EditAssignmentScope expected)
        => Assert.Equal(expected, Create(EditAssignmentSlice.Create(sheet, table, null, null), GrantLevel.Read).Scope);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData(GrantLevel.Read)]
    [InlineData(GrantLevel.Submit)]
    [InlineData(GrantLevel.Approve)]
    public void Місяць_період_і_ексклюзивність_лише_для_Write(GrantLevel level)
    {
        Assert.Throws<ArgumentException>(() => Create(EditAssignmentSlice.Create("F1", null, 1, null), level));
        Assert.Throws<ArgumentException>(() => Create(EditAssignmentSlice.Create("F1", null, null, 202601), level));
        Assert.Throws<ArgumentException>(() => Create(EditAssignmentSlice.Create("F1", null, null, null), level, exclusive: true));

        var plain = Create(EditAssignmentSlice.Create("F1", null, null, null), level);
        Assert.Throws<ArgumentException>(() => plain.Reconfigure(isExclusive: true, null, 10, T0));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData(GrantLevel.None)]
    [InlineData(GrantLevel.Manage)]
    public void Manage_і_None_не_видаються(GrantLevel level)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => Create(EditAssignmentSlice.Create(null, null, null, null), level));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Передача_власника_змінює_власника_і_повертає_попереднього()
    {
        var document = new Document(1, "DOC-1", createdByUserId: 5, T0);

        var previous = document.TransferOwnership(9, byUserId: 5, T0.AddHours(1));

        Assert.Equal(5, previous);
        Assert.Equal(9, document.CreatedByUserId);
        Assert.Equal(5, document.ModifiedByUserId);
        Assert.Equal(T0.AddHours(1), document.ModifiedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Передача_тому_самому_власнику_відхиляється()
    {
        var document = new Document(1, "DOC-1", createdByUserId: 5, T0);

        Assert.Throws<InvalidOperationException>(() => document.TransferOwnership(5, 5, T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.TransferOwnership(0, 5, T0));
    }
}
