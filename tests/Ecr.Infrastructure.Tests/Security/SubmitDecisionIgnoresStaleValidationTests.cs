// tests/Ecr.Infrastructure.Tests/Security/SubmitDecisionIgnoresStaleValidationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// T2-01 (тестувальний прохід №2): рішення про подання не залежить від ЗБЕРЕЖЕНОГО
/// результату останнього «Перевірити». Було: застарілий <c>ErrorCount &gt; 0</c> давав
/// <c>403 ECR-ACCS-0403</c> «domain rule», хоч дані вже виправлено. Блокування по
/// помилках валідації робить <c>SubmitSheetHandler</c> свіжим прогоном (422).
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: повернути в <c>CanSubmitAsync</c> читання останнього
/// <c>ValidationResult.ErrorCount</c> у <c>hasBlockingErrors</c> — тест червоніє
/// (<see cref="EditDenyReason.BusinessRule"/>).
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitDecisionIgnoresStaleValidationTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "T2-01")]
    public async Task Збережена_помилка_валідації_не_блокує_рішення_про_подання()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();
        await using (var seed = sql.CreateContext())
        {
            seed.ValidationResults.Add(new ValidationResult(
                document.DocumentId, document.PeriodKey.Value, DateTime.UtcNow.AddMinutes(-5), 1, 0, 0, "[]"));
            await seed.SaveChangesAsync();
        }

        var profile = new AccessBuilder()
            .Grant(ResourceKind.Project, document.ProjectId, GrantLevel.Read)
            .Grant(ResourceKind.Sheet, document.SheetDefId, GrantLevel.Submit)
            .Build();

        await using var db = sql.CreateContext();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.UserId.Returns(profile.UserId);
        currentUser.GroupSids.Returns([]);
        var service = new AccessDecisionService(
            db,
            Substitute.For<IMetadataCache>(),
            new AccessProfileCache(memory),
            new TestClock(DateTime.UtcNow),
            currentUser,
            Substitute.For<IWorkflowStore>());

        var decision = await service.CanSubmitAsync(
            profile, document.DocumentId, document.SheetDefId, document.PeriodKey, CancellationToken.None);

        Assert.True(decision.IsAllowed, $"Відмова за застарілим збереженим результатом: {decision.Reason}");
    }
}
