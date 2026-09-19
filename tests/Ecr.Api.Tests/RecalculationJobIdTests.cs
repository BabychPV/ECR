using Ecr.Api.Controllers;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// BE-05: відповідь на запис комірок несе ідентифікатор задачі перерахунку —
/// і цей ідентифікатор РОБОЧИЙ для того, хто правив.
/// </summary>
/// <remarks>
/// ⛔ Предмет — ШОВ між двома обробниками, а не кожен із них окремо.
/// <c>PatchCellsHandler</c> ставить задачу, <c>GetJobStatusHandler</c> пускає до
/// стану ЧУЖОЇ задачі лише за правом <c>System.ViewHealth</c> (Q-156). Доки
/// постановка не називала автора, обидва були «правильні» поодинці, а разом
/// давали дефект, який видно лише звідси: сервер віддає редакторові
/// ідентифікатор і сам же відповідає <c>403</c> на перше опитування цього
/// ідентифікатора. Рівно <c>S-28</c>, тільки для перерахунку замість експорту.
///
/// ⚠ Тому планувальник тут — ПІДРОБКА ЗІ СТАНОМ, а не substitute без зв'язків:
/// вона запам'ятовує <c>createdByUserId</c>, з яким її покликали, і віддає його
/// назад через <c>GetCreatedByUserIdAsync</c> — так само, як це робить
/// <c>QuartzJobScheduler</c>. Substitute, налаштований віддавати автора окремо
/// від постановки, перевіряв би власне налаштування: він був би зелений і тоді,
/// коли обробник автора не передає взагалі.
///
/// ⚠ Обидва обробники СПРАВЖНІ і ділять один <see cref="IAccessDecisionService"/>
/// з ОДНИМ профілем без <c>System.ViewHealth</c>: права редактора на запис
/// комірок і брак права на стан системи — це той самий користувач, а не два
/// різні налаштування.
/// </remarks>
public sealed class RecalculationJobIdTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int VolumeColumnId = 11;

    /// <summary>Редактор: правку робить він, і стан задачі читає теж він.</summary>
    private const int Editor = 9;

    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly RecordingJobScheduler _jobs = new();

    public RecalculationJobIdTests()
    {
        var column = new ColumnDef(
            tableDefId: 3, EcrCode.Create("Volume"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Volume" }), 1, CellDataType.Decimal);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, VolumeColumnId);

        var sheet = new SheetDef(
            templateVersionId: 2, EcrCode.Create("Water"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Water" }), 1);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("Main"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Main" }), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, 3);
        table.AddColumn(column);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: 2, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [VolumeColumnId] = column },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Editor);
        _user.Language.Returns("en");

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(
                 TableInstance, DocumentId: 700, TableDefId: 3, TemplateVersionId: 2, PeriodKey: Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);
        _methodologies.GetMethodologyIdsBoundToTableAsync(3, Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { ["7001001"] = "0x0A" });
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = 1001L });

        // ⛔ ОДИН профіль на обидва обробники, і в ньому НЕМАЄ
        // `System.ViewHealth`: саме це право й перевіряє `GetJobStatusHandler`
        // перед тим, як віддати стан. Додати його тут означало б знищити
        // предмет тесту — з ним 200 віддався б і без автора задачі.
        _access.BuildProfileAsync(Editor, Arg.Any<CancellationToken>())
               .Returns(new AccessBuilder { UserId = Editor }.Build());

        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>
               {
                   [new CellAddress(PeriodKey.Parse(Period), 1001L, VolumeColumnId)] = EditDecision.Allow(),
               });

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                   .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());
    }

    /// <summary>
    /// ⛔ Головне твердження файлу: ідентифікатор із відповіді на <c>PATCH</c>
    /// читається АВТОРОМ ПРАВКИ без <c>System.ViewHealth</c> — <c>200</c>, а не
    /// <c>403</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "BE-05")]
    public async Task Автор_правки_читає_стан_власного_перерахунку_без_ViewHealth()
    {
        var response = await Patch().ConfigureAwait(true);

        // Ідентифікатор є і він не порожній: клієнтові нема за чим стежити,
        // якщо поле порожнє, — а стежити є за чим, задачу щойно поставили.
        Assert.False(string.IsNullOrWhiteSpace(response.RecalculationJobId));

        var ok = Assert.IsType<OkObjectResult>(
            (await Controller().Get(response.RecalculationJobId!, CancellationToken.None)
                               .ConfigureAwait(true)).Result);

        Assert.Equal(200, ok.StatusCode);

        var status = Assert.IsType<JobStatus>(ok.Value);
        Assert.Equal(response.RecalculationJobId, status.JobId);
    }

    /// <summary>
    /// Той самий ідентифікатор ЧУЖОМУ без права — <c>403</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Опудало проти «дозволили всім»: якби автор читався 200-кою через те,
    /// що перевірку права просто прибрали, цей тест упав би. Без нього
    /// попередній доводив би лише «двері відчинені», а не «відчинені саме
    /// авторові».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "BE-05")]
    public async Task Чужий_без_ViewHealth_стан_того_самого_перерахунку_не_читає()
    {
        const int Stranger = 10;

        var response = await Patch().ConfigureAwait(true);

        _user.UserId.Returns(Stranger);
        _access.BuildProfileAsync(Stranger, Arg.Any<CancellationToken>())
               .Returns(new AccessBuilder { UserId = Stranger }.Build());

        var denied = await Assert.ThrowsAsync<Application.Errors.AccessDeniedException>(
            () => Controller().Get(response.RecalculationJobId!, CancellationToken.None))
            .ConfigureAwait(true);

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
    }

    private async Task<PatchCellsResponse> Patch()
        => await new PatchCellsHandler(
                _cells, _rows, _documents, _periods, _metadata, _access,
                new Application.Validation.ValidationEngine(new RealFormulaEngine()),
                _methodologies, _registries, _audit, _jobs, _uow, _user, _clock)
            .HandleAsync(
                new PatchCellsRequest(
                    TableInstance, Period, "UserEdit",
                    [new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12500m)])]),
                CancellationToken.None)
            .ConfigureAwait(true);

    private JobsController Controller() => new(
        new GetJobStatusHandler(_jobs, _access, _user, new FakeUiStringCatalog()),
        new ListJobsHandler(_jobs, _access, _user),
        new RestartJobHandler(_jobs, _access, _user),
        new CancelJobHandler(_jobs, _access, _user));

    /// <summary>
    /// Планувальник, який ПАМ'ЯТАЄ, кого назвали автором задачі.
    /// </summary>
    /// <remarks>
    /// ⛔ Ідентифікатор навмисно містить <c>#</c> — так його будує
    /// <c>QuartzJobScheduler</c> (<c>IRecalculationJob#42</c>). Саме цей символ
    /// коштував кроку 17 у <c>smoke.ps1</c>: в URL він починає фрагмент, тож
    /// клієнт зобов'язаний кодувати сегмент. Підробка з «чистим» ідентифікатором
    /// мовчки прибрала б цю властивість із тесту.
    /// </remarks>
    private sealed class RecordingJobScheduler : IBackgroundJobScheduler
    {
        private readonly Dictionary<string, int?> _authors = new(StringComparer.Ordinal);
        private int _next;

        public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob
        {
            _next += 1;
            var jobId = $"{typeof(TJob).Name}#{_next}";
            _authors[jobId] = createdByUserId;

            return Task.FromResult(jobId);
        }

        public Task<string> EnqueueExclusiveAsync<TJob>(
            string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob
            => EnqueueAsync<TJob>(payload, ct, createdByUserId);

        public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct)
            where TJob : IBackgroundJob
            => Task.CompletedTask;

        public Task CancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> RestartAsync(string jobId, CancellationToken ct) => Task.FromResult(true);

        public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct)
            => Task.FromResult(_authors.ContainsKey(jobId)
                ? new JobStatus(jobId, "Running", 0, null, null)
                : new JobStatus(jobId, "Unknown", 0, null, null));

        public Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct)
            => Task.FromResult(_authors.TryGetValue(jobId, out var author) ? author : null);

        public Task<IReadOnlyList<JobSummary>> ListRecentAsync(
            JobListFilter filter, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JobSummary>>([]);
    }
}
