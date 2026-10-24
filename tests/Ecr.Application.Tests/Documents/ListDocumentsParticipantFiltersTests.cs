using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// D-446: документи, відкриті лише наданням учасника (D-443), під фільтрами <c>hasLateEdits</c>/<c>resultsStale</c> —
/// обробник передає в сховище ВЛАСНІ межі учасника (пари «документ, аркуш/таблиця/колонка», ті самі, що фільтр стану
/// D-444), а частково схований документ — у переліку виключених із фільтра застарілості.
/// </summary>
/// <remarks>
/// Мутаційний доказ (локально, не в коміті):
/// <list type="bullet">
/// <item>повернути умову <c>filter.HasLateEdits is null &amp;&amp; filter.ResultsStale is null</c> у визначенні
/// <c>participant</c> → червоніють <c>Під_hasLateEdits_учасник_передає_власні_межі_документа</c> і тест застарілості
/// (<c>Participation == null</c>);</item>
/// <item>не передати <c>HiddenTables</c>/<c>HiddenColumns</c> → червоніє перший тест (надання на таблицю ховає решту
/// таблиць і колонок аркуша, а межа лише за аркушами їх би не закрила);</item>
/// <item>прибрати <c>NarrowedDocumentIds</c> → червоніє <c>Під_resultsStale_частково_схований_документ_виключається</c>;</item>
/// <item>повернути безумовне <c>HasLateEdits = false</c> для документа-надання → червоніє
/// <c>Позначка_документа_надання_лишається_а_без_меж_знімається</c>; прибрати умову <c>!bounded.Contains</c> так, щоб
/// позначка завжди лишалась, → той самий тест (документ без меж зберіг би позначку).</item>
/// </list>
/// </remarks>
public sealed class ListDocumentsParticipantFiltersTests
{
    private const int UserId = 7;
    private const int RoleProject = 10;
    private const int GrantProject = 20;
    private const int Period = 202601;
    private const long Partial = 55;
    private const long Whole = 56;
    private const long Unbounded = 57;

    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly SheetDef _sheet1;
    private readonly SheetDef _sheet2;
    private readonly TableDef _table2;
    private readonly ColumnDef _column2;

    public ListDocumentsParticipantFiltersTests()
    {
        var template = new TemplateBuilder();
        _sheet1 = template.Sheet("S1");
        var table1 = template.Table(_sheet1, "T1");
        template.Column(table1, "C1");
        _sheet2 = template.Sheet("S2");
        _table2 = template.Table(_sheet2, "T2");
        _column2 = template.Column(_table2, "C2");
        var snapshot = template.Build();

        _user.UserId.Returns(UserId);
        var profile = new AccessBuilder { UserId = UserId, Participant = true }
            .Permission(ListDocumentsHandler.Permission)
            .Grant(ResourceKind.Project, RoleProject, GrantLevel.Read)
            .Build();
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(profile);

        // 55 — лише таблиця аркуша S1 (S2 схований); 56 — весь документ; 57 — надання є в документі, але в межах
        // (`ActiveParticipationsAsync`) його немає: з'явилось між читанням меж і запитом сторінки.
        var grants = new Dictionary<long, IReadOnlyList<ParticipationGrant>>
        {
            [Partial] = [new ParticipationGrant(GrantLevel.Read, "S1", "T1", null)],
            [Whole] = [new ParticipationGrant(GrantLevel.Read, null, null, null)],
            [Unbounded] = [new ParticipationGrant(GrantLevel.Read, null, null, null)],
        };
        _access.ActiveParticipationsAsync(Arg.Any<AccessProfile>(), Arg.Any<CancellationToken>()).Returns(
            new Dictionary<long, IReadOnlyList<ParticipationGrant>> { [Partial] = grants[Partial], [Whole] = grants[Whole] });
        _access.ReadScopesAsync(Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, DocumentReadScope>)call.Arg<IReadOnlyCollection<long>>()
                .Where(grants.ContainsKey)
                .ToDictionary(
                    id => id,
                    id => DocumentReadScope.ForParticipant(call.Arg<AccessProfile>(), GrantProject, snapshot, grants[id])));
        _access.ParticipatedDocumentsAsync(
                Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<long>)call.Arg<IReadOnlyCollection<long>>().Where(grants.ContainsKey).ToHashSet());
    }

    [Fact]
    [Trait("Decision", "D-446")]
    public async Task Під_hasLateEdits_учасник_передає_власні_межі_документа()
    {
        Page([]);

        await Handler().HandleAsync(null, Period, null, mine: false, hasLateEdits: true, new CursorRequest(50), default);

        await _documents.Received(1).ListAsync(
            Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(),
            Arg.Is<DocumentListFilter>(f => f.HasLateEdits == true && OwnBoundsOfPartialOnly(f)),
            Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Decision", "D-446")]
    public async Task Під_resultsStale_частково_схований_документ_виключається()
    {
        Page([]);

        await Handler().HandleAsync(
            null, Period, null, mine: false, hasLateEdits: null, query: null, resultsStale: false, staleBy: null,
            new CursorRequest(50), default);

        // ⛔ Обидва напрямки: і `false` не віддає частково схованого (інакше «не застарілі» = решта).
        await _documents.Received(1).ListAsync(
            Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(),
            Arg.Is<DocumentListFilter>(f => f.ResultsStale == false
                                            && f.Participation != null
                                            && f.Participation.NarrowedDocumentIds != null
                                            && f.Participation.NarrowedDocumentIds.SequenceEqual(new[] { Partial })),
            Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Decision", "D-446")]
    public async Task Без_фільтра_застарілості_переліку_виключених_немає()
    {
        Page([]);

        await Handler().HandleAsync(null, Period, null, mine: false, hasLateEdits: null, new CursorRequest(50), default);

        await _documents.Received(1).ListAsync(
            Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(),
            Arg.Is<DocumentListFilter>(f => f.Participation != null && f.Participation.NarrowedDocumentIds == null),
            Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Decision", "D-446")]
    public async Task Позначка_документа_надання_лишається_а_без_меж_знімається()
    {
        Page([Doc(Partial), Doc(Whole), Doc(Unbounded)]);

        var result = await Handler().HandleAsync(null, Period, null, mine: false, hasLateEdits: null, new CursorRequest(50), default);

        // Позначку сховище порахувало у власних межах учасника — обробник її не гасить.
        Assert.True(result.Items.Single(d => d.Id == Partial).HasLateEdits);
        Assert.True(result.Items.Single(d => d.Id == Whole).HasLateEdits);

        // ⛔ Межі для 57 не пораховані — позначка рахувалась без них, тож не віддається.
        Assert.False(result.Items.Single(d => d.Id == Unbounded).HasLateEdits);

        // Застарілість: цілком видимий — як є; частково схований — не визначена (`DocumentSheetVisibility.For`).
        Assert.True(result.Items.Single(d => d.Id == Whole).ResultsStale);
        Assert.Null(result.Items.Single(d => d.Id == Partial).ResultsStale);
    }

    private ListDocumentsHandler Handler()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc));
        return new ListDocumentsHandler(_documents, _access, _user, Substitute.For<IDocumentListSummaryStore>(), clock);
    }

    private void Page(IReadOnlyList<DocumentSummary> items)
        => _documents.ListAsync(
                Arg.Any<int?>(), Arg.Any<PeriodKeyFilter>(), Arg.Any<DocumentListFilter>(),
                Arg.Any<CursorRequest>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<DocumentSummary>(items, NextCursor: null, TotalCount: null));

    private static DocumentSummary Doc(long id)
        => new(id, GrantProject, $"DOC-{id}", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 2,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["S1"] = "Draft", ["S2"] = "Draft" },
            HasLateEdits: true,
            IncludedSheetCodes: ["S1", "S2"],
            ResultsStale: true);

    // Дерево виразу не бере кортежних літералів, тож порівняння — окремим методом.
    private bool OwnBoundsOfPartialOnly(DocumentListFilter f)
        => f.Participation is { } p
           && p.HiddenSheets is { } sheets && sheets.SequenceEqual(new[] { (Partial, _sheet2.Id) })
           && p.HiddenTables is { } tables && tables.SequenceEqual(new[] { (Partial, _table2.Id) })
           && p.HiddenColumns is { } columns && columns.SequenceEqual(new[] { (Partial, _column2.Id) });
}
