// tests/Ecr.Infrastructure.Tests/Jobs/ExcelImportJobDeferralTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// R7-Y2-02 (X6-02): фоновий імпорт, що впирається в аркуш, який саме подається
/// (<c>409 ECR-DOC-4091 sheetBeingSubmitted</c> від <c>EnterEditNoWaitAsync</c>), не провалюється й не
/// витрачає спробу ретраю, а відкладає себе (<see cref="JobDeferredException"/>).
/// </summary>
/// <remarks>
/// ⛔ Мутація, що валить тест: прибрати перехоплення в <see cref="ExcelImportJob.ExecuteAsync"/>.
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class ExcelImportJobDeferralTests
{
    private const long DocumentId = 700;
    private const int UserId = 9;

    private readonly IExcelImporter _importer = Substitute.For<IExcelImporter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ExcelImportJobDeferralTests()
    {
        _user.UserId.Returns(UserId);
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "R7-Y2-02")]
    public async Task Аркуш_що_подається_відкладає_задачу_а_не_провалює()
    {
        _importer.ApplyAsync(DocumentId, "tok", Arg.Any<IReadOnlyList<ImportOverwriteRow>?>(), Arg.Any<CancellationToken>())
                 .ThrowsAsync(Busy("err.ECR-DOC-4091.sheetBeingSubmitted"));

        var deferred = await Assert.ThrowsAsync<JobDeferredException>(
            () => Job().ExecuteAsync(new ExcelImportTask(DocumentId, "tok"), Substitute.For<IJobProgress>(), CancellationToken.None));

        Assert.Equal(RecalculationDocumentLock.DeferDelay, deferred.Delay);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "R7-Y2-02")]
    public async Task Інша_відмова_ECR_DOC_4091_не_відкладається()
    {
        // ⚠ «Структуру змінено» — той самий код, але не черга: перегляд застарів, повтор не допоможе.
        _importer.ApplyAsync(DocumentId, "tok", Arg.Any<IReadOnlyList<ImportOverwriteRow>?>(), Arg.Any<CancellationToken>())
                 .ThrowsAsync(Busy("err.ECR-DOC-4091.structureChanged"));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Job().ExecuteAsync(new ExcelImportTask(DocumentId, "tok"), Substitute.For<IJobProgress>(), CancellationToken.None));
    }

    private ExcelImportJob Job() => new(_importer, new JobActorScope(), _access, _user);

    private static ConcurrencyConflictException Busy(string messageKey)
        => new(ErrorCodes.SheetBusy, "busy", new Dictionary<string, object?> { ["messageKey"] = messageKey });

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p1", UserId = UserId, SecurityStamp = "s",
        Permissions = new HashSet<string> { ApplyImportHandler.Permission },
        Grants = new Dictionary<string, Ecr.Domain.Enums.GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };
}
