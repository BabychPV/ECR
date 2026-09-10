using System.Diagnostics;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Директива №11, T10 #45: <c>ApplyImportHandler</c> застосовував КОЖЕН
/// імпорт синхронно, незалежно від розміру — на відміну від
/// <see cref="ExportDocumentHandler"/>, який уже давно йде в чергу
/// безумовно.
/// </summary>
/// <remarks>
/// ⛔ D-134. Великий diff застосовувався б у тілі HTTP-запиту: секунди чи
/// десятки секунд утримання з'єднання й потоку. Тест
/// <see cref="Великий_diff_не_блокує_запит_довше_за_поріг_часу"/> ловить
/// САМЕ ЦЕ — прибери перевірку порогу (`pendingCount > LargeImportThreshold`)
/// у <see cref="ApplyImportHandler.HandleAsync"/>, і він почервоніє, бо
/// виклик почне чекати на повільний <c>ApplyAsync</c> синхронно.
/// </remarks>
public sealed class ApplyImportThresholdTests
{
    private const long DocumentId = 701;
    private const string Token = "token-large";

    private readonly IExcelImporter _importer = Substitute.For<IExcelImporter>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ApplyImportThresholdTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Document.Import").Build());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
    }

    private ApplyImportHandler Handler() => new(_importer, _jobs, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-45")]
    public async Task Малий_diff_застосовується_синхронно()
    {
        _importer.CountPendingChangesAsync(Token, Arg.Any<CancellationToken>())
            .Returns(ApplyImportHandler.LargeImportThreshold);

        var response = new PatchCellsResponse(3, new Dictionary<string, string>(), []);
        _importer.ApplyAsync(DocumentId, Token, Arg.Any<CancellationToken>()).Returns(response);

        var result = await Handler().HandleAsync(DocumentId, Token, CancellationToken.None);

        Assert.Null(result.JobId);
        Assert.NotNull(result.Response);
        Assert.Equal(3, result.Response!.AppliedCells);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueAsync<IExcelImportJob>(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-45")]
    public async Task Великий_diff_іде_в_чергу_а_не_застосовується_синхронно()
    {
        _importer.CountPendingChangesAsync(Token, Arg.Any<CancellationToken>())
            .Returns(ApplyImportHandler.LargeImportThreshold + 1);

        _jobs.EnqueueAsync<IExcelImportJob>(
                Arg.Any<ExcelImportTask>(), Arg.Any<CancellationToken>(), 9)
            .Returns("job-large-1");

        var result = await Handler().HandleAsync(DocumentId, Token, CancellationToken.None);

        Assert.Equal("job-large-1", result.JobId);
        Assert.Null(result.Response);
        await _importer.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-45")]
    public async Task Великий_diff_не_блокує_запит_довше_за_поріг_часу()
    {
        // ⚠ Затримка на порядок довша за будь-який розумний бюджет
        // синхронного HTTP-обробника (`tz/08 §8.2` — секунди, не десятки).
        // Обробник має ПОВЕРНУТИСЯ задовго до того, як ця затримка мине —
        // саме тому, що для великого diff він узагалі не чекає на ApplyAsync.
        var slowApply = TimeSpan.FromSeconds(5);
        const int BudgetMs = 500;

        _importer.CountPendingChangesAsync(Token, Arg.Any<CancellationToken>())
            .Returns(ApplyImportHandler.LargeImportThreshold + 1);

        _importer.ApplyAsync(DocumentId, Token, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(slowApply);
                return new PatchCellsResponse(0, new Dictionary<string, string>(), []);
            });

        _jobs.EnqueueAsync<IExcelImportJob>(Arg.Any<ExcelImportTask>(), Arg.Any<CancellationToken>(), 9)
            .Returns("job-large-2");

        var stopwatch = Stopwatch.StartNew();
        var result = await Handler().HandleAsync(DocumentId, Token, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(
            stopwatch.ElapsedMilliseconds < BudgetMs,
            $"Застосування великого імпорту тривало {stopwatch.ElapsedMilliseconds} мс — "
            + $"поріг мав відправити його в чергу, не чекати {slowApply.TotalSeconds} с синхронно.");
        Assert.Equal("job-large-2", result.JobId);
    }
}
