using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
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
/// виклик почне чекати на <c>ApplyAsync</c>, що ніколи не завершується
/// (AN-111: ворота замість стінного часу).
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
    [Trait("Requirement", "ФВ-4.5")]
    public async Task Малий_diff_застосовується_синхронно()
    {
        _importer.CountPendingChangesAsync(DocumentId, Token,Arg.Any<CancellationToken>())
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
    [Trait("Requirement", "ФВ-4.5")]
    public async Task Великий_diff_іде_в_чергу_а_не_застосовується_синхронно()
    {
        _importer.CountPendingChangesAsync(DocumentId, Token,Arg.Any<CancellationToken>())
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
    [Trait("Finding", "L1-20")]
    public async Task Перегляд_іншого_документа_відмовляє_до_черги_і_нічого_не_ставить()
    {
        _importer.CountPendingChangesAsync(DocumentId, Token, Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new BusinessRuleException("ECR-IMP-0422", "Перегляд належить іншому документу."));

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(DocumentId, Token, CancellationToken.None));

        await _jobs.DidNotReceiveWithAnyArgs().EnqueueAsync<IExcelImportJob>(default, default, default);
        await _importer.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "T10-45")]
    public async Task Великий_diff_не_блокує_запит_довше_за_поріг_часу()
    {
        // ⚠ AN-111: раніше тут був Stopwatch і бюджет 500 мс проти
        // `Task.Delay(5 с)` — у CI раз вийшло 671 мс (холодний JIT, зайнятий
        // агент) при правильному коді. Стінний час замінено ворітьми:
        // `ApplyAsync` повертає задачу, яка НІКОЛИ не завершиться, доки тест
        // сам її не відпустить. Великий diff не чекає на неї взагалі, тож
        // виклик обробника вже завершений у момент повернення (усі інші
        // залежності — підставні, з готовими задачами). Прибери поріг —
        // обробник почне чекати на ворота, і `IsCompleted` буде false
        // детерміновано, без жодного таймауту.
        var gate = new TaskCompletionSource<PatchCellsResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _importer.CountPendingChangesAsync(DocumentId, Token,Arg.Any<CancellationToken>())
            .Returns(ApplyImportHandler.LargeImportThreshold + 1);

        _importer.ApplyAsync(DocumentId, Token, Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);

        _jobs.EnqueueAsync<IExcelImportJob>(Arg.Any<ExcelImportTask>(), Arg.Any<CancellationToken>(), 9)
            .Returns("job-large-2");

        var call = Handler().HandleAsync(DocumentId, Token, CancellationToken.None);
        try
        {
            Assert.True(
                call.IsCompleted,
                "Застосування великого імпорту чекає на ApplyAsync — "
                + "поріг мав відправити його в чергу, а не тримати запит синхронно.");

            var result = await call;
            Assert.Equal("job-large-2", result.JobId);
            Assert.Null(result.Response);
            await _importer.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, default);
        }
        finally
        {
            // Відпускаємо ворота, щоб мутант (синхронне застосування) не
            // лишив висячу задачу після червоного тесту.
            gate.TrySetResult(new PatchCellsResponse(0, new Dictionary<string, string>(), []));
        }
    }
}
