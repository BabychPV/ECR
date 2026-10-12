using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Доменні інваріанти передачі місяця (EA-1): канонічний ключ зрізу, склад виконавців і цикл
/// <c>Assigned → Filled → Returned → Filled → Validated</c>.
/// </summary>
/// <remarks>
/// Мутації: прибрати скидання валідації в <c>ReturnRecipient</c> / <c>AddRecipient</c> - червоніють
/// відповідні тести; прибрати перевірку <c>AllFilled</c> у <c>Validate</c> - червоніє тест «не всі
/// заповнили»; змінити порядок полів у <c>EditAssignmentSlice.Key</c> - червоніє тест ключа.
/// </remarks>
public sealed class EditAssignmentTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddHours(1);

    private static EditAssignment Create(params int[] recipients)
    {
        var assignment = new EditAssignment(
            documentId: 7,
            EditAssignmentSlice.Create("F1", null, 1, null),
            isExclusive: true,
            delegatedByUserId: 10,
            handoffId: Guid.NewGuid(),
            createdByUserId: 10,
            utcNow: T0);

        foreach (var user in recipients)
        {
            assignment.AddRecipient(user, 10, T0);
        }

        return assignment;
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData(null, null, null, null, "S=*|T=*|M=*|P=*")]
    [InlineData("F1", null, 1, null, "S=F1|T=*|M=1|P=*")]
    [InlineData("F1", "T2", null, 202601, "S=F1|T=T2|M=*|P=202601")]
    [InlineData("  F1 ", null, 12, 202612, "S=F1|T=*|M=12|P=202612")]
    public void Ключ_зрізу_канонічний(string? sheet, string? table, int? month, int? period, string expected)
        => Assert.Equal(expected, EditAssignmentSlice.Create(sheet, table, (byte?)month, period).Key);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Таблиця_без_аркуша_місяць_поза_межами_і_службові_символи_відхиляються()
    {
        Assert.Throws<ArgumentException>(() => EditAssignmentSlice.Create(null, "T1", null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => EditAssignmentSlice.Create(null, null, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => EditAssignmentSlice.Create(null, null, 13, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => EditAssignmentSlice.Create(null, null, null, 12));
        Assert.Throws<ArgumentException>(() => EditAssignmentSlice.Create("A|B", null, null, null));
        Assert.Throws<ArgumentException>(() => EditAssignmentSlice.Create("*", null, null, null));
        Assert.Throws<ArgumentException>(() => EditAssignmentSlice.Create(new string('x', 65), null, null, null));
        Assert.Throws<ArgumentException>(() => EditAssignmentSlice.Create("   ", null, null, null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Нова_передача_не_валідована_ексклюзивна_і_без_виконавців()
    {
        var assignment = Create();

        Assert.Equal(EditAssignmentValidationStatus.NotValidated, assignment.ValidationStatus);
        Assert.True(assignment.IsExclusive);
        Assert.Empty(assignment.Recipients);
        Assert.False(assignment.AllFilled);
        Assert.Equal("S=F1|T=*|M=1|P=*", assignment.SliceKey);
        Assert.Equal(assignment.Slice.Key, assignment.SliceKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Повторне_додавання_виконавця_ігнорується_а_ліміт_діє()
    {
        var assignment = Create(1);

        Assert.False(assignment.AddRecipient(1, 10, T0));
        Assert.Single(assignment.Recipients);

        for (var user = 2; user <= EditAssignment.MaxRecipients; user++)
        {
            Assert.True(assignment.AddRecipient(user, 10, T0));
        }

        Assert.Throws<InvalidOperationException>(() => assignment.AddRecipient(1000, 10, T0));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Цикл_Передано_Заповнено_Повернуто_Заповнено_Валідовано()
    {
        var assignment = Create(1, 2);

        Assert.True(assignment.FillRecipient(1, T1));
        Assert.False(assignment.FillRecipient(1, T1.AddMinutes(5))); // ідемпотентно
        Assert.Equal(T1, assignment.FindRecipient(1)!.FilledAt);
        Assert.False(assignment.AllFilled);
        Assert.Throws<InvalidOperationException>(() => assignment.Validate(10, T1));

        assignment.FillRecipient(2, T1);
        Assert.True(assignment.AllFilled);

        assignment.ReturnRecipient(2, 10, "  Перевірте березень  ", T1.AddHours(1));
        var returned = assignment.FindRecipient(2)!;
        Assert.Equal(EditAssignmentRecipientStatus.Returned, returned.Status);
        Assert.Equal("Перевірте березень", returned.ReturnReason);
        Assert.Equal(1, returned.ReturnCount);
        Assert.Equal(10, returned.ReturnedByUserId);
        Assert.False(assignment.AllFilled);

        Assert.True(assignment.FillRecipient(2, T1.AddHours(2)));
        Assert.True(assignment.Validate(10, T1.AddHours(3)));
        Assert.Equal(EditAssignmentValidationStatus.Validated, assignment.ValidationStatus);
        Assert.Equal(T1.AddHours(3), assignment.ValidatedAt);
        Assert.Equal(10, assignment.ValidatedByUserId);
        Assert.False(assignment.Validate(10, T1.AddHours(4))); // повтор — без змін
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Повернення_і_новий_виконавець_скидають_валідацію()
    {
        var assignment = Create(1);
        assignment.FillRecipient(1, T1);
        assignment.Validate(10, T1);

        assignment.AddRecipient(2, 10, T1.AddMinutes(1));
        Assert.Equal(EditAssignmentValidationStatus.NotValidated, assignment.ValidationStatus);
        Assert.Null(assignment.ValidatedAt);
        Assert.Null(assignment.ValidatedByUserId);

        assignment.FillRecipient(2, T1);
        assignment.Validate(10, T1);
        assignment.ReturnRecipient(1, 10, "Доопрацювати", T1.AddMinutes(2));
        Assert.Equal(EditAssignmentValidationStatus.NotValidated, assignment.ValidationStatus);
        Assert.Null(assignment.ValidatedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Повернути_можна_лише_заповнене_і_лише_з_причиною()
    {
        var assignment = Create(1);

        Assert.Throws<InvalidOperationException>(() => assignment.ReturnRecipient(1, 10, "причина", T1));
        assignment.FillRecipient(1, T1);

        Assert.Throws<ArgumentException>(() => assignment.ReturnRecipient(1, 10, "   ", T1));
        Assert.Throws<ArgumentException>(
            () => assignment.ReturnRecipient(1, 10, new string('x', EditAssignment.MaxNoteLength + 1), T1));
        Assert.Throws<InvalidOperationException>(() => assignment.ReturnRecipient(99, 10, "причина", T1));
        Assert.Throws<InvalidOperationException>(() => assignment.FillRecipient(99, T1));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Прибрання_виконавця_і_набір_із_лімітами()
    {
        var assignment = Create(1, 2);
        Assert.True(assignment.RemoveRecipient(1, 10, T1));
        Assert.False(assignment.RemoveRecipient(1, 10, T1));
        Assert.Single(assignment.Recipients);

        var set = new EditorSet(10, "  Відділ 5 ", T0);
        Assert.Equal("Відділ 5", set.Name);
        Assert.True(set.AddMember(1));
        Assert.False(set.AddMember(1));
        Assert.Throws<ArgumentException>(() => new EditorSet(10, "   ", T0));
        Assert.Throws<ArgumentException>(() => new EditorSet(10, new string('x', EditorSet.MaxNameLength + 1), T0));
    }
}
