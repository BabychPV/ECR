// tests/Ecr.Application.Tests/Documents/DocumentCalcStatusCacheTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents.CalcStatus;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ D-577 (огляд M7): опитування чипа «Стан розрахунку» не б'є застарілість, актуальний прогін і провал щоразу — короткий
/// кеш на документ і період; зміна складу живих задач (старт, завершення) дає новий ключ, тож чип не відстає від задачі.
/// </summary>
/// <remarks>Мутація (локально): не класти факти в кеш (<c>cache.Set</c>) — червоний (2 звернення замість 1).</remarks>
public sealed class DocumentCalcStatusCacheTests
{
    private const long DocumentId = 577_701;
    private const int UserId = 7;
    private const int Period = 202609;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-577")]
    public async Task Повторне_опитування_з_кешу_а_нова_задача_скидає_кеш()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = UserId }
            .Permission(GetDocumentCalcStatusHandler.Permission)
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>()).Returns(EditDecision.Allow());
        access.DocumentProjectIdAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(AccessBuilder.ProjectId);
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var store = Substitute.For<IDocumentCalcStatusStore>();
        store.StaleMonthsAsync(DocumentId, AccessBuilder.ProjectId, Period, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<StaleMonthFact>());
        store.ActiveAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ActiveRecalculationFact>(), Array.Empty<ActiveRecalculationFact>(),
                [new ActiveRecalculationFact("job-1", "Queued", 0, null, null, null, DateTime.UnixEpoch, null, null, null, null)]);
        var stale = new DocumentStaleMonths(
            store, Substitute.For<IPeriodStore>(), Substitute.For<IWorkflowStore>(), Substitute.For<IDocumentStore>(),
            Substitute.For<IDocumentSheetCopyStore>());
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc));
        using var cache = new MemoryCache(new MemoryCacheOptions());

        var handler = new GetDocumentCalcStatusHandler(access, user, store, stale, clock, cache: cache);
        await handler.HandleAsync(DocumentId, Period, CancellationToken.None);
        await handler.HandleAsync(DocumentId, Period, CancellationToken.None);
        await store.Received(1).StaleMonthsAsync(DocumentId, AccessBuilder.ProjectId, Period, Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await store.Received(1).LastFailureAsync(DocumentId, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());

        await handler.HandleAsync(DocumentId, Period, CancellationToken.None);
        await store.Received(2).StaleMonthsAsync(DocumentId, AccessBuilder.ProjectId, Period, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }
}
