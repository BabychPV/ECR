// tests/Ecr.Application.Tests/Sources/SourceEventMapTestBase.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// Спільна підготовка тестів обробників мапінгу подій (HSE301 A6): підроблені порти, документ, динамічна таблиця,
/// колонки й довідник, готові команди.
/// </summary>
public abstract class SourceEventMapTestBase
{
    protected const int Actor = 9;
    protected const int EntityId = 5;
    protected const long DocumentId = 42;
    protected const int ProjectId = 10;
    protected const int VersionId = 100;
    protected const int TableId = 10;
    protected const int RegistryId = 7;

    protected SourceEventMapTestBase()
    {
        User.UserId.Returns(Actor);
        Uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        Profile(GrantLevel.Manage);

        // Видимість документа — те саме правило, що й у службі доступу (SeesDocumentsOf).
        Access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<AccessProfile>().SeesDocumentsOf(ProjectId)
                ? EditDecision.Allow()
                : EditDecision.Deny(EditDenyReason.NoGrant));

        Sources.FindSourceEntityAsync(EntityId, Arg.Any<CancellationToken>())
            .Returns(new SourceEntity(1, "FlareEvent", RegistrySourceKind.External));
        Sources.UnitExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        Table = NewTable(TableRowMode.Dynamic);
        Columns[1] = Column("START_AT", CellDataType.Date, 1);
        Columns[2] = Column("END_AT", CellDataType.Date, 2);
        Columns[3] = Column("EVENT_NAME", CellDataType.String, 3);
        Columns[4] = Column("CATEGORY", CellDataType.Lookup, 4, lookupRegistry: RegistryId);

        Store.FindDocumentAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(new EventMapDocumentInfo(ProjectId, VersionId));
        Store.FindTargetTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(_ => new EventMapTableInfo(Table, VersionId));
        Store.FindColumnsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<int, ColumnDef>)Columns
                .Where(c => call.Arg<IReadOnlyCollection<int>>().Contains(c.Key))
                .ToDictionary(c => c.Key, c => c.Value));

        // 999 — запису немає; 888 — запис ІНШОГО довідника; решта — довідника колонки.
        Store.FindRegistryEntryDefsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, int>)call.Arg<IReadOnlyCollection<long>>()
                .Where(id => id != 999)
                .ToDictionary(id => id, id => id == 888 ? RegistryId + 1 : RegistryId));
        Store.AddMapAsync(Arg.Any<SourceEventMap>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var map = call.Arg<SourceEventMap>();
                SetId(map, 77);
                return map;
            });
    }

    protected ISourceEventMapStore Store { get; } = Substitute.For<ISourceEventMapStore>();

    protected ICollectionStore Sources { get; } = Substitute.For<ICollectionStore>();

    protected IAccessDecisionService Access { get; } = Substitute.For<IAccessDecisionService>();

    protected ICurrentUser User { get; } = Substitute.For<ICurrentUser>();

    protected IUnitOfWork Uow { get; } = Substitute.For<IUnitOfWork>();

    protected IAuditWriter Audit { get; } = Substitute.For<IAuditWriter>();

    protected IClock Clock { get; } = Substitute.For<IClock>();

    protected Dictionary<int, ColumnDef> Columns { get; } = [];

    protected TableDef Table { get; set; }

    protected void Profile(GrantLevel level)
        => Access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission("Integration.Manage").Grant(ResourceKind.Project, ProjectId, level).Build());

    protected SourceEventMap NewMap()
    {
        var map = SourceEventMap.Create(
            EntityId,
            DocumentId,
            Table,
            [
                new(Columns[1], "$start"),
                new(Columns[2], "$end"),
                new(Columns[3], "$name"),
                new(Columns[4], "Category", SourceEventAttributeScope.Event, SourceEventValueKind.LookupByCode),
            ],
            SourceEventVolumeMode.EventAttribute);
        SetId(map, 77);
        return map;
    }

    protected static CreateSourceEventMapCommand Command(SourceEventFieldInput? category = null)
        => new(
            EntityId,
            DocumentId,
            TableId,
            SourceEventVolumeMode.EventAttribute,
            null,
            null,
            null,
            [Field(1, "$start"), Field(2, "$end"), Field(3, "$name"), category ?? Category(SourceEventValueKind.LookupByCode)]);

    protected static UpdateSourceEventMapCommand UpdateCommand()
        => new(SourceEventVolumeMode.None, true, null, null, null, [Field(1, "$start"), Field(2, "$end")]);

    protected static SourceEventFieldInput Field(int column, string attribute)
        => new(column, attribute, SourceEventAttributeScope.Event, SourceEventValueKind.Direct, null, null, null);

    protected static SourceEventFieldInput Category(SourceEventValueKind kind, params SourceEventValueInput[] values)
        => new(4, "Category", SourceEventAttributeScope.Event, kind, null, null, values);

    protected static TableDef NewTable(TableRowMode mode)
    {
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("FLARE_EVENTS"), Text("Flare events"), 1, TableLayoutKind.PerPeriodInstance, mode);
        SetId(table, TableId);
        return table;
    }

    protected static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static ColumnDef Column(string code, CellDataType type, int id, int? lookupRegistry = null)
    {
        var column = new ColumnDef(TableId, EcrCode.Create(code), Text(code), id, type);
        SetId(column, id);
        if (lookupRegistry is not null)
        {
            typeof(ColumnDef).GetProperty(nameof(ColumnDef.LookupRegistryDefId))!.SetValue(column, lookupRegistry);
        }

        return column;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
