// tests/Ecr.Application.Tests/Workflow/WorkflowTransactionTests.ArchivedRecall.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// B69 (G1): виняток «місяць відкрито для документа», що пережив архівацію, не повертає аркуш у роботу —
/// справжня база, справжній <c>WorkflowStore</c>, справжній обробник.
/// </summary>
/// <remarks>
/// ⛔ Доти <c>ClosedPeriodGuard</c> пропускав <c>Recall</c> у закритому місяці, щойно для документа діяв виняток
/// (приймальна 08.11 (i)), і стан проєкту не дивився зовсім: подання в АРХІВОВАНОМУ проєкті поверталося в Draft.
/// Червоне без фіксу: прибрати перевірку архіву в <c>ClosedPeriodGuard</c> — відкликання проходить (аркуш Draft).
/// </remarks>
public sealed partial class WorkflowTransactionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B69-G1")]
    public async Task Відкликання_в_архівованому_проєкті_відмовляє_навіть_із_винятком_документа()
    {
        var world = await ArrangeAsync().ConfigureAwait(true);
        await SubmitWithoutRouteAsync(world).ConfigureAwait(true);

        await ArchiveWithSurvivingDocumentReopenAsync(world).ConfigureAwait(true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => RecallAsync(world, UserId, RecallReason)).ConfigureAwait(true);

        Assert.Equal("ECR-PRD-4223", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-4223.projectArchived", error.Details!["messageKey"]);
        Assert.Equal(DocumentStatus.Submitted, await StatusAsync(world).ConfigureAwait(true));
        Assert.Single(await EventsAsync(world).ConfigureAwait(true));
    }

    /// <summary>
    /// Місяць закрито, для документа діє виняток (три доби від <see cref="Now"/>), проєкт заархівовано — стан, у який
    /// система потрапляла до B69 (архівація не бачила винятків документа).
    /// </summary>
    private async Task ArchiveWithSurvivingDocumentReopenAsync(World world)
    {
        await using var db = CreateContext();

        var name = $"b69_{Guid.NewGuid():N}"[..20];
        var user = new Ecr.Domain.Entities.Security.User(name, name, AuthProvider.Local);
        user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash("Archived-Recall-B69-2026!"));
        db.Users.Add(user);

        var project = await db.Projects.Include(p => p.Periods)
            .FirstAsync(p => p.Id == world.ProjectId).ConfigureAwait(false);
        project.Activate(Now);
        foreach (var period in project.Periods)
        {
            period.AdvanceTo(PeriodState.Closed, Now);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        db.DocumentPeriodReopens.Add(DocumentPeriodReopen.Open(
            world.DocumentId, PeriodKeyValue, Now.AddDays(3), "B69: виправлення до архівації", user.Id, Now));

        project.Archive(Now);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
