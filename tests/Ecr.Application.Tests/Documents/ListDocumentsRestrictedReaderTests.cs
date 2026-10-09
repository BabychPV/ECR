using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Перелік документів для читача з обмеженнями нижче проєкту (<c>Deny Sheet</c> лише в одному з проєктів на тій самій
/// версії шаблону): межі йдуть у сховище ПАРАМИ «проєкт, Id» (N1-01).
/// </summary>
/// <remarks>
/// Мутація (CI): повернути плаский перелік Id у <c>DocumentSheetVisibility.HiddenFilterAsync</c> → тест пар червоніє;
/// прибрати звуження до <c>projectId</c> чи передачу меж в <c>ApplyAsync</c> → тести лічильника звернень червоніють (N1-02).
/// </remarks>
public sealed class ListDocumentsRestrictedReaderTests
{
    private const int ProjectA = 10;
    private const int ProjectB = 11;
    private const int Period = 202601;

    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IDocumentListSummaryStore _samples = Substitute.For<IDocumentListSummaryStore>();

    private readonly SheetDef _sheet;
    private readonly TableDef _table;
    private readonly ColumnDef _column;

    public ListDocumentsRestrictedReaderTests()
    {
        // Одна версія шаблону на обидва проєкти: ідентифікатори аркуша, таблиці й колонки спільні.
        var template = new TemplateBuilder();
        _sheet = template.Sheet("S1");
        _table = template.Table(_sheet, "T1");
        _column = template.Column(_table, "C1");
        var snapshot = template.Build();

        _user.UserId.Returns(7);

        // Deny на аркуш - лише в проєкті A (роль з областю: ключ Scoped - проєкт).
        var plain = new AccessBuilder { UserId = 7 }
            .Permission(ListDocumentsHandler.Permission)
            .Grant(ResourceKind.Project, ProjectA, GrantLevel.Read)
            .Grant(ResourceKind.Project, ProjectB, GrantLevel.Read)
            .Build();
        var profile = new AccessProfile
        {
            CacheKey = plain.CacheKey,
            UserId = plain.UserId,
            SecurityStamp = plain.SecurityStamp,
            Permissions = plain.Permissions,
            Grants = plain.Grants,
            Denies = plain.Denies,
            RoleIds = plain.RoleIds,
            Scoped = new Dictionary<int, ScopedProjectAccess>
            {
                [ProjectA] = new(
                    new Dictionary<string, GrantLevel>(),
                    new HashSet<string> { $"{ResourceKind.Sheet}:{_sheet.Id}" },
                    new HashSet<int>(),
                    new HashSet<string>()),
            },
        };
        _access.BuildProfileAsync(7, Arg.Any<CancellationToken>()).Returns(profile);

        _samples.SampleDocumentPerProjectAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<int, long>)call.Arg<IReadOnlyCollection<int>>()
                .ToDictionary(p => p, p => (long)(1000 + p)));
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var project = (int)(call.Arg<long>() - 1000);
                return DocumentReadScope.For(call.Arg<AccessProfile>(), project, snapshot);
            });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Межі_читача_йдуть_у_сховище_парами_і_Deny_в_A_не_ховає_аркуш_у_B()
    {
        Page([]);

        await Handler().HandleAsync(null, Period, "Rejected", false, null, new CursorRequest(50), default);

        // ⛔ N1-01: аркуш схований ЛИШЕ в проєкті A; плаский перелік [sheet] ховав би його й у документах B.
        await _documents.Received(1).ListAsync(
            Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(),
            Arg.Is<DocumentListFilter>(f => HiddenOnlyInA(f)),
            Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Запит_з_projectId_будує_межі_лише_цього_проєкту_і_один_раз()
    {
        Page([Doc(1, ProjectA)]);

        await Handler().HandleAsync(ProjectA, Period, null, false, null, new CursorRequest(50), default);

        // ⛔ N1-02: раніше межі будувались по ВСІХ видимих проєктах (зразок на проєкт + ReadScope на кожен), а потім ще раз
        // по документах сторінки: 2N+1 звернень навіть при заданому projectId.
        await _samples.Received(1).SampleDocumentPerProjectAsync(
            Arg.Is<IReadOnlyCollection<int>>(p => p.Count == 1 && p.Contains(ProjectA)), Arg.Any<CancellationToken>());
        Assert.Equal(1, ReadScopeCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Перелік_по_кількох_проєктах_будує_межі_раз_на_проєкт_а_не_двічі()
    {
        Page([Doc(1, ProjectA), Doc(2, ProjectB)]);

        var result = await Handler().HandleAsync(null, Period, null, false, null, new CursorRequest(50), default);

        // Межі з HiddenFilterAsync (по проєкту) ідуть в ApplyAsync без другого кола ReadScopeAsync: 2 звернення, не 4.
        Assert.Equal(2, ReadScopeCalls());

        // І застосовані правильно: у A аркуш схований, у B - видимий.
        Assert.Equal(0, result.Items.Single(d => d.ProjectId == ProjectA).SheetCount);
        Assert.Equal(1, result.Items.Single(d => d.ProjectId == ProjectB).SheetCount);
    }

    private int ReadScopeCalls()
        => _access.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAccessDecisionService.ReadScopeAsync));

    private static DocumentSummary Doc(long id, int projectId)
        => new(id, projectId, $"DOC-{id}", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["S1"] = "Draft" },
            IncludedSheetCodes: ["S1"]);

    // Дерево виразу не бере кортежних літералів, тож порівняння - окремим методом.
    private bool HiddenOnlyInA(DocumentListFilter f)
        => f.HiddenSheetDefIds is { } sheets && sheets.SequenceEqual(new[] { (ProjectA, _sheet.Id) })
           && f.HiddenTableDefIds is { } tables && tables.SequenceEqual(new[] { (ProjectA, _table.Id) })
           && f.HiddenColumnDefIds is { } columns && columns.SequenceEqual(new[] { (ProjectA, _column.Id) });

    private ListDocumentsHandler Handler() => new(_documents, _access, _user, _samples);

    private void Page(IReadOnlyList<DocumentSummary> items)
        => _documents.ListAsync(
                Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(), Arg.Any<DocumentListFilter>(),
                Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<DocumentSummary>(items, NextCursor: null, TotalCount: null));
}
