using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Виправлення за аудитом EA-1a (EA-C): термін дії, скидання валідації при знятті виконавця,
/// канонічний регістр ключа зрізу, склад набору.
/// </summary>
public sealed class EditAssignmentAuditFixesTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    private static EditAssignment Create(DateTime? expiresAt = null, string sheet = "F1")
        => new(
            documentId: 7,
            EditAssignmentSlice.Create(sheet, null, 1, null),
            isExclusive: true,
            delegatedByUserId: 10,
            handoffId: Guid.NewGuid(),
            createdByUserId: 10,
            utcNow: T0,
            expiresAt: expiresAt);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Термін_дії_не_в_минулому_і_не_далі_за_стелю()
    {
        Assert.NotNull(Create(T0.AddDays(10)));
        Assert.NotNull(Create(T0.AddDays(EditAssignment.MaxLifetimeDays)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(T0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(T0.AddMinutes(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(T0.AddDays(EditAssignment.MaxLifetimeDays + 1)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Прибрання_виконавця_скидає_валідацію()
    {
        var assignment = Create();
        assignment.AddRecipient(1, 10, T0);
        assignment.AddRecipient(2, 10, T0);
        assignment.FillRecipient(1, T0.AddMinutes(1));
        assignment.FillRecipient(2, T0.AddMinutes(1));
        assignment.Validate(10, T0.AddMinutes(2));
        Assert.Equal(EditAssignmentValidationStatus.Validated, assignment.ValidationStatus);

        Assert.True(assignment.RemoveRecipient(2, 10, T0.AddMinutes(3)));

        Assert.Equal(EditAssignmentValidationStatus.NotValidated, assignment.ValidationStatus);
        Assert.Null(assignment.ValidatedAt);
        Assert.Null(assignment.ValidatedByUserId);
    }

    [Theory]
    [InlineData("f1", "F1")]
    [InlineData("Sheet_a", "SHEET_A")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ключ_зрізу_не_залежить_від_регістру_коду(string lower, string upper)
        => Assert.Equal(Create(sheet: upper).SliceKey, Create(sheet: lower).SliceKey);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Склад_набору_замінюється_а_назва_змінюється()
    {
        var set = new EditorSet(10, "Dept", T0);
        set.AddMember(1);
        set.AddMember(2);

        set.ReplaceMembers([2, 3, 3], T0.AddMinutes(1));
        set.Rename("  Dept 6 ", T0.AddMinutes(1));

        Assert.Equal([2, 3], set.Members.Select(m => m.UserId).Order().ToArray());
        Assert.Equal("Dept 6", set.Name);
        Assert.Throws<InvalidOperationException>(
            () => set.ReplaceMembers(Enumerable.Range(1, EditorSet.MaxMembers + 1), T0));
    }
}
