using Ecr.Application.Documents.EditAssignments;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents.EditAssignments;

/// <summary>RC20 D-2: правило <c>visibility</c> виконавця (<c>EditAssignmentMapper.VisibilityOf</c>).</summary>
public sealed class RecipientVisibilityTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private static EditAssignment Assignment()
        => new(
            documentId: 7, EditAssignmentSlice.Create(null, null, 1, null), isExclusive: true, delegatedByUserId: 10,
            handoffId: Guid.NewGuid(), createdByUserId: 10, utcNow: Now);

    private static DocumentReadScope VisibleScope()
    {
        var profile = new AccessBuilder().Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read).Build();
        var snapshot = new TemplateVersionSnapshot(
            2, 0, [], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>());

        return DocumentReadScope.For(profile, AccessBuilder.ProjectId, snapshot);
    }

    private static EditAssignmentUserRef User(bool windows)
        => new(5, "User", null, IsActive: true, IsWindowsAccount: windows);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Немає_словника_чи_запису_для_виконавця_це_Unknown()
    {
        var a = Assignment();

        Assert.Equal(RecipientVisibility.Unknown, EditAssignmentMapper.VisibilityOf(a, 5, User(false), null));
        Assert.Equal(
            RecipientVisibility.Unknown,
            EditAssignmentMapper.VisibilityOf(a, 5, User(false), new Dictionary<int, DocumentReadScope?>()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Видимий_NotVisible_для_локального_і_Unknown_для_доменного_облікового_запису()
    {
        var a = Assignment();
        var visible = new Dictionary<int, DocumentReadScope?> { [5] = VisibleScope() };
        var hidden = new Dictionary<int, DocumentReadScope?> { [5] = null };

        Assert.Equal(RecipientVisibility.Visible, EditAssignmentMapper.VisibilityOf(a, 5, User(false), visible));
        Assert.Equal(RecipientVisibility.Visible, EditAssignmentMapper.VisibilityOf(a, 5, User(true), visible));
        Assert.Equal(RecipientVisibility.NotVisible, EditAssignmentMapper.VisibilityOf(a, 5, User(false), hidden));
        // Групи AD чужої людини невідомі (P-02): «не бачить» за профілем без груп — лише «невідомо».
        Assert.Equal(RecipientVisibility.Unknown, EditAssignmentMapper.VisibilityOf(a, 5, User(true), hidden));
        Assert.Equal(RecipientVisibility.NotVisible, EditAssignmentMapper.VisibilityOf(a, 5, null, hidden));
    }
}
