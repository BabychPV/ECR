// tests/Ecr.Application.Tests/Templates/DraftVersionLockTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// C5: кожна структурна правка чернетки бере блок рядка версії першою дією
/// транзакції й перевіряє «ще чернетка» вже ПІСЛЯ нього.
/// </summary>
/// <remarks>
/// ⛔ Сутність версії в кожному випадку тут — ЧЕРНЕТКА (так її прочитав
/// обробник до транзакції), а блок повертає <c>Published</c>: рівно стан
/// «публікація закомітилася між читанням і записом». До C5 обробник
/// перевіряв лише застарілу сутність і писав у вже опубліковану версію.
/// Тепер — відмова <c>ECR-TMPL-0409</c> і жодного запису чи аудиту.
///
/// ⚠ Детермінована гонка на справжній базі — <c>PublishDraftEditRaceApiTests</c>
/// (Ecr.Api.Tests); тут — порядок викликів у кожному обробнику.
/// </remarks>
public sealed class DraftVersionLockTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IStyleCatalog _styles = Substitute.For<IStyleCatalog>();
    private readonly IRepository<TemplateVersion, int> _versions = Substitute.For<IRepository<TemplateVersion, int>>();
    private readonly IRepository<TableRelationDef, int> _relations = Substitute.For<IRepository<TableRelationDef, int>>();

    private readonly List<string> _events = [];
    private readonly TemplateVersion _draft;
    private readonly TableDef _table;
    private readonly ColumnDef _formulaColumn;

    public DraftVersionLockTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _events.Add("tx");
                return call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1));
            });
        _store.When(x => x.LockVersionForUpdateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()))
            .Do(_ => _events.Add("lock"));
        _uow.When(x => x.SaveChangesAsync(Arg.Any<CancellationToken>()))
            .Do(_ => _events.Add("save"));
        _audit.When(x => x.WriteStructureChangeAsync(Arg.Any<StructureChangeRecord>(), Arg.Any<CancellationToken>()))
            .Do(_ => _events.Add("audit"));

        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("S1");
        _table = builder.Table(sheet, "T1");
        builder.Column(_table, "C1");
        _formulaColumn = builder.Column(_table, "CF", CellDataType.Formula);
        builder.Row(_table, "R1");
        builder.Formula(_table, "[C1] * 2", column: _formulaColumn);
        var other = builder.Table(sheet, "T9");
        _draft = builder.Version();

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        _versions.FindAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);

        var relation = new TableRelationDef(
            EcrCode.Create("REL1"), _table.Id, other.Id,TableRelationKind.Reference, "{}");
        _store.FindTableRelationAsync(1, "REL1", Arg.Any<CancellationToken>()).Returns(relation);

        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal)));

        // ⛔ Публікація закомітилася між читанням сутності й блоком.
        _store.LockVersionForUpdateAsync(1, Arg.Any<CancellationToken>())
            .Returns(TemplateVersionStatus.Published);
    }

    public static TheoryData<string> Handlers() =>
    [
        "SaveSheet", "DeleteSheet", "SaveTable", "DeleteTable",
        "SaveColumn", "DeleteColumn", "SaveRow", "DeleteRow",
        "SaveHeaderField", "SaveStyle", "DeleteFormula", "DeleteTableRelation",
    ];

    [Theory]
    [MemberData(nameof(Handlers))]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Опублікована_під_блоком_версія_відхиляє_правку_без_запису(string handler)
    {
        Assert.Equal(TemplateVersionStatus.Draft, _draft.Status);

        var error = await Assert.ThrowsAsync<DomainException>(() => Run(handler));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0409.structurallyFrozen", error.Details!["messageKey"]);

        // Блок узято, і нічого після нього: ні запису, ні аудиту.
        await _store.Received(1).LockVersionForUpdateAsync(1, Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
        Assert.Equal(["tx", "lock"], _events);
    }

    [Theory]
    [MemberData(nameof(Handlers))]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Чернетка_під_блоком_пишеться_і_блок_іде_першим(string handler)
    {
        _store.LockVersionForUpdateAsync(1, Arg.Any<CancellationToken>())
            .Returns(TemplateVersionStatus.Draft);

        await Run(handler);

        // ⛔ Транзакція відкрита, і ПЕРША дія в ній — блок; запис і аудит — лише після.
        Assert.True(
            _events.Count >= 3 && _events[0] == "tx" && _events[1] == "lock" && _events.Contains("save"),
            string.Join(" → ", _events));
        Assert.Single(_events, e => e == "lock");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Виведена_з_обігу_під_блоком_теж_заморожена()
    {
        _store.LockVersionForUpdateAsync(1, Arg.Any<CancellationToken>())
            .Returns(TemplateVersionStatus.Deprecated);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => DraftVersionLock.EnsureDraftUnderLockAsync(_store, _draft, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
    }

    private static Dictionary<string, string> En(string value)
        => new(StringComparer.OrdinalIgnoreCase) { ["en"] = value };

    private Task Run(string handler)
    {
        var ct = CancellationToken.None;
        var classifier = new ChangeClassifier();

        return handler switch
        {
            "SaveSheet" => new SaveSheetDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, "S2", new SaveSheetDefCommand(En("S2"), null, null, false, true), ct),
            "DeleteSheet" => new DeleteSheetDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, "S1", ct),
            "SaveTable" => new SaveTableDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, "S1", "T2", new SaveTableDefCommand(
                    En("T2"), null, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed, null), ct),
            "DeleteTable" => new DeleteTableDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, "S1", "T1", ct),
            "SaveColumn" => new SaveColumnDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user, _units)
                .HandleAsync(1, _table.Id, "C2", new SaveColumnDefCommand(
                    En("C2"), null, CellDataType.Decimal, false, false, false,
                    null, null, null, null, null, null, null, null), ct),
            "DeleteColumn" => new DeleteColumnDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, _table.Id, "C1", ct),
            "SaveRow" => new SaveRowDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, _table.Id, "R2", new SaveRowDefCommand(En("R2"), null, RowKind.Item, null, false), ct),
            "DeleteRow" => new DeleteRowDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, _table.Id, "R1", ct),
            "SaveHeaderField" => new SaveHeaderFieldDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, "H1", new SaveHeaderFieldDefCommand(En("H1"), null, CellDataType.String, false, null), ct),
            "SaveStyle" => new SaveStyleDefHandler(_styles, _store, _uow, _access, _user)
                .HandleAsync(1, "ST1", new SaveStyleDefCommand(
                    "Calibri", 11m, false, false, null, null, null, null, null, false, null), ct),
            "DeleteFormula" => new DeleteFormulaDefHandler(_store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, _table.Id, FormulaScope.Column,
                    _formulaColumn.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ct),
            "DeleteTableRelation" => new DeleteTableRelationHandler(
                    _versions, _relations, _store, classifier, _metadataCache, _audit, _uow, _clock, _access, _user)
                .HandleAsync(1, "REL1", ct),
            _ => throw new ArgumentOutOfRangeException(nameof(handler), handler, null),
        };
    }
}
