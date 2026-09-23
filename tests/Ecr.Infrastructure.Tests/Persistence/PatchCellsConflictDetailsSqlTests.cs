using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>BE-06</c>, доказ у формі, заданій директивою: два послідовні
/// <c>PATCH</c> з ОДНІЄЮ <c>BaseVersion</c> → <c>409</c>, у якому названо чуже
/// значення, ім'я автора й момент його правки.
/// </summary>
/// <remarks>
/// ⛔ Чому проти справжньої СУБД. До цієї роботи відповідь на конфлікт несла
/// <c>null</c>, <c>""</c> і <c>clock.UtcNow</c> — і жоден юніт-тест цього не
/// ловив, бо ловити було нічим: підміна віддала б те, що їй сказали. Тут
/// значення справді лежить у <c>doc.CellValue</c>, автор — у <c>sec.User</c>, а
/// момент — у <c>aud.CellChange</c>, і весь ланцюг проходить той самий шлях, що
/// й у продуктиві.
///
/// ⛔ Годинник ДРУГОГО письменника зсунутий на годину вперед — рівно та умова,
/// яку директива називає мутацією: «повернути <c>clock.UtcNow</c> → тест із
/// замороженим годинником, зсунутим на годину, падає». Без зсуву обидва
/// варіанти дали б однакове число.
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchCellsConflictDetailsSqlTests(SqlServerFixture sql)
{
    /// <summary>Значення, яке встигає записати перший аналітик.</summary>
    private const decimal FirstWriterValue = 111m;

    /// <summary>Значення другого — того, чия <c>baseVersion</c> застаріла.</summary>
    private const decimal SecondWriterValue = 222m;

    private const string DisplayName = "A. Serikbayev";
    private const string UserName = "aserikbayev";

    /// <summary>Момент ПЕРШОЇ правки.</summary>
    private static readonly DateTime FirstMoment = new(2026, 2, 1, 9, 15, 0, DateTimeKind.Utc);

    /// <summary>Момент, у який працює ДРУГИЙ, — на годину пізніше.</summary>
    private static readonly DateTime SecondMoment = new(2026, 2, 1, 10, 15, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "BE-06")]
    public async Task Другий_PATCH_дістає_409_із_чужим_значенням_іменем_автора_і_моментом_правки()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);
        var rowKey = await FirstRowKeyAsync(doc);
        var firstUserId = await CreateUserAsync();

        // ⚠ ОДНА й та сама версія для обох: саме так виглядає реальність —
        // двоє відкрили ту саму сітку й бачать той самий `baseVersion`.
        var baseVersion = await RowVersionBase64Async(doc, rowKey);

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();

        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);

        var first = BuildHandler(firstDb, bulk, new FixedClock(FirstMoment), doc, firstUserId);
        await first.HandleAsync(
            Request(doc, rowKey, baseVersion, FirstWriterValue), CancellationToken.None);

        // Другий письменник приходить із ТІЄЮ САМОЮ версією — і на годину пізніше.
        var second = BuildHandler(secondDb, bulk, new FixedClock(SecondMoment), doc, userId: firstUserId + 1);

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => second.HandleAsync(
                Request(doc, rowKey, baseVersion, SecondWriterValue), CancellationToken.None));

        Assert.Equal(ErrorCodes.CellConflict, conflict.ErrorCode);

        var conflicts = Assert.IsAssignableFrom<IReadOnlyList<CellConflictDto>>(conflict.Details!["conflicts"]);
        var details = Assert.Single(conflicts);

        Assert.Equal(rowKey, details.RowKey);
        Assert.Equal(CodeOf(doc, 2), details.ColumnCode);

        // ⛔ ЧУЖЕ значення — те, що ПЕРШИЙ поклав у базу, а не порожнеча.
        Assert.Equal(FirstWriterValue, details.TheirValue);
        Assert.Equal(SecondWriterValue, details.YourValue);

        // ⛔ ІМ'Я автора з `sec.User.DisplayName`, а не порожній рядок і не логін.
        Assert.Equal(DisplayName, details.TheirUser);
        Assert.Equal("UserEdit", details.TheirOrigin);

        // ⛔ Момент ПЕРШОЇ правки, у межах секунди від запису в журналі, а не
        // поточний час сервера. Різниця видима саме тому, що годинники рознесені
        // на годину: `clock.UtcNow` дав би 10:15 замість 9:15.
        var written = await AuditMomentAsync(doc);
        Assert.NotNull(details.TheirChangedAt);
        Assert.True(
            Math.Abs((details.TheirChangedAt!.Value - written).TotalSeconds) < 1,
            $"Момент чужої правки {details.TheirChangedAt:O} розійшовся з журналом {written:O}.");
        Assert.NotEqual(SecondMoment, details.TheirChangedAt);

        // ⚠ Перелік не обрізано — отже лічильник каже про нуль, а не мовчить.
        Assert.Equal("0", conflict.Details!["moreConflicts"]);
    }

    private static PatchCellsRequest Request(TestDocument doc, string rowKey, string baseVersion, decimal value)
        => new(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(rowKey, baseVersion, [new PatchCell(CodeOf(doc, 2), value)])]);

    /// <summary>Обробник на реальних сховищах, включно з реальним читачем журналу.</summary>
    private static PatchCellsHandler BuildHandler(
        EcrDbContext db, BulkCellLoader bulk, IClock clock, TestDocument doc, int userId)
    {
        var cellStore = new NormalizedCellStore(db);
        var rowStore = new RowStore(db, bulk, clock);
        var documentStore = new DocumentStore(db);
        var auditWriter = new AuditWriter(db);
        var auditReader = new AuditReader(db);
        var uow = new UnitOfWork(db);

        var column1 = ColumnDefFor(doc, 1, CellDataType.String);
        var column2 = ColumnDefFor(doc, 2, CellDataType.Decimal);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        var table = new TableDef(sheet.Id, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(column1);
        table.AddColumn(column2);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>
            {
                [doc.ColumnDefIds[0]] = column1,
                [doc.ColumnDefIds[1]] = column2,
            },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var access = Substitute.For<IAccessDecisionService>();
        var profile = new AccessProfile
        {
            CacheKey = "p", UserId = userId, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        };
        access.BuildProfileAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanEditSliceAsync(Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<CancellationToken>())
              .Returns(doc.RowIds
                  .SelectMany(rowId => doc.ColumnDefIds
                      .Select(columnId => new CellAddress(doc.PeriodKey, rowId, columnId)))
                  .ToDictionary(address => address, _ => EditDecision.Allow()));
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId,
                  Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<string, NewRowAccess>());

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(doc.TableDefId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        var registries = Substitute.For<IRegistryStore>();
        registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                  .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new PatchCellsHandler(
            cellStore, rowStore, documentStore, periods, metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, headers, auditWriter, auditReader, jobs, uow, user, clock);
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int ordinal, CellDataType type)
    {
        var id = doc.ColumnDefIds[ordinal - 1];
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C{ordinal}_{id}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Col{ordinal}" }), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private static string CodeOf(TestDocument doc, int ordinal)
        => $"C{ordinal}_{doc.ColumnDefIds[ordinal - 1]}";

    private async Task<string> FirstRowKeyAsync(TestDocument doc)
        => await ScalarAsync<string>(
            $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.RowIds[0]}");

    /// <summary>Момент, який журнал справді зберіг для чужої правки.</summary>
    private async Task<DateTime> AuditMomentAsync(TestDocument doc)
        => await ScalarAsync<DateTime>(
            $"SELECT TOP (1) ChangedAt FROM aud.CellChange WHERE DocumentId = {doc.DocumentId} "
            + $"AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]} "
            + "ORDER BY ChangedAt DESC");

    private async Task<string> RowVersionBase64Async(TestDocument doc, string rowKey)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT RowVersion FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} " +
            $"AND RowKey = N'{rowKey}'";
        var bytes = (byte[])(await command.ExecuteScalarAsync())!;
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Заводить автора першої правки в <c>sec.User</c>.</summary>
    private async Task<int> CreateUserAsync()
    {
        var unique = Guid.NewGuid().ToString("N");

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sec.[User]
                (UserName, DisplayName, Provider, WindowsSid, SecurityStamp, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@userName, @displayName, 0, @sid, @stamp, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@userName", $"{UserName}-{unique}");
        command.Parameters.AddWithValue("@displayName", DisplayName);
        command.Parameters.AddWithValue("@sid", $"S-1-5-21-{unique}");
        command.Parameters.AddWithValue("@stamp", unique);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Годинник із фіксованим часом — детермінований момент правки.</summary>
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
