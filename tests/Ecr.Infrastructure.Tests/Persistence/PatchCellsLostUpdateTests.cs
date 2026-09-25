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
/// Тихе загублене оновлення (lost update) на сітці документа: двоє аналітиків
/// правлять ТУ САМУ комірку, обидва отримують <c>200</c>, другий мовчки затирає
/// першого, а рядок аудиту стверджує перехід значення, якого не було.
/// </summary>
/// <remarks>
/// ⛔ Причина, яку стереже цей тест: перевірка <c>baseVersion</c> жила в пам'яті
/// C# (<c>PatchCellsHandler.EnsureNoVersionConflicts</c>) і виконувалась ПОЗА
/// транзакцією запису, а самі записи не несли жодного предиката на
/// <c>RowVersion</c> (<c>MERGE doc.CellValue WITH (HOLDLOCK)</c> звіряв лише
/// адресу комірки, <c>RowStore.TouchRowsAsync</c> фільтрував лише за <c>Id</c>).
/// Між «прочитали версію» і «записали» лишалося вікно, у яке вміщався ВЕСЬ
/// чужий батч.
///
/// ⚠ Гонка відтворюється ДЕТЕРМІНОВАНО, а не двома паралельними задачами з
/// надією на збіг: перемикання вставлене рівно у вікно між перевіркою версій і
/// записом. Точка — <c>IPeriodStore.FindPeriodStateAsync</c>: обробник кличе її
/// у <c>DetermineIsLateEditAsync</c> ПІСЛЯ <c>EnsureNoVersionConflicts</c> і ДО
/// <c>PersistChangesAsync</c>. Тест від справжнього планувальника потоків не
/// залежить і «іноді зеленим» бути не може.
///
/// ⚠ Обидва письменники мають ВЛАСНИЙ <c>EcrDbContext</c> (отже, власне
/// з'єднання й власну транзакцію) — інакше це був би один письменник, що
/// викликає себе рекурсивно, і жодної гонки тут не було б.
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchCellsLostUpdateTests(SqlServerFixture sql)
{
    /// <summary>Значення, яке встигає записати перший аналітик.</summary>
    private const decimal FirstWriterValue = 111m;

    /// <summary>Значення другого — того, чия <c>baseVersion</c> застаріла у вікні гонки.</summary>
    private const decimal SecondWriterValue = 222m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Другий_письменник_зі_застарілою_версією_не_затирає_першого()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);
        var rowKey = await FirstRowKeyAsync(doc);

        // ⚠ ОДНА й та сама версія для обох: саме так виглядає реальність —
        // двоє відкрили ту саму сітку й бачать той самий `baseVersion`.
        var baseVersion = await RowVersionBase64Async(doc, rowKey);

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();

        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);

        var firstWriter = BuildHandler(firstDb, bulk, clock, doc, interleave: null);

        // Другий письменник спиняється рівно у вікні «версію перевірено — ще не
        // записано» і пропускає вперед першого; після цього продовжує свій
        // запис із версією, яка вже НЕ чинна.
        var firstWriterDone = false;
        var secondWriter = BuildHandler(secondDb, bulk, clock, doc, interleave: () =>
        {
            if (firstWriterDone)
            {
                return;
            }

            firstWriterDone = true;
            firstWriter
                .HandleAsync(Request(doc, rowKey, baseVersion, FirstWriterValue), CancellationToken.None)
                .GetAwaiter().GetResult();
        });

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => secondWriter.HandleAsync(
                Request(doc, rowKey, baseVersion, SecondWriterValue), CancellationToken.None));

        Assert.True(firstWriterDone, "Перемикання не спрацювало: перший письменник не встиг записати.");
        Assert.Equal(ErrorCodes.CellConflict, conflict.ErrorCode);

        // ⛔ Головне твердження: у базі лишається значення ПЕРШОГО. Доти, доки
        // перевірка версії робилась у пам'яті поза транзакцією, тут лежало
        // `222` — другий запис проходив із `200`, і перша правка зникала без
        // жодного сліду.
        var stored = await ScalarOrNullAsync<decimal?>(
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]}");
        Assert.Equal(FirstWriterValue, stored);

        // ⛔ Друга половина дефекту, не менш дорога за саме затирання: журнал
        // аудиту стверджував перехід, якого не було. Запис другого письменника
        // казав би «було порожньо, стало 222», хоча насправді в комірці на той
        // момент лежало 111 — тобто аудит брехав про ОБИДВА кінці переходу.
        var lyingAudit = await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId} " +
            $"AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[1]} " +
            $"AND NewValue = '{SecondWriterValue}'");
        Assert.Equal(0, lyingAudit);
    }

    private static PatchCellsRequest Request(TestDocument doc, string rowKey, string baseVersion, decimal value)
        => new(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(rowKey, baseVersion, [new PatchCell(CodeOf(doc, 2), value)])]);

    /// <summary>
    /// Обробник на реальних сховищах; <paramref name="interleave"/> — гачок, що
    /// спрацьовує рівно один раз у вікні між перевіркою версій і записом.
    /// </summary>
    private static PatchCellsHandler BuildHandler(
        EcrDbContext db, BulkCellLoader bulk, IClock clock, TestDocument doc, Action? interleave)
    {
        var cellStore = new NormalizedCellStore(db);
        var rowStore = new RowStore(db, bulk, clock);
        var documentStore = new DocumentStore(db);
        var auditWriter = new AuditWriter(db);
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

        // ⚠ Ось те саме вікно. `DetermineIsLateEditAsync` — остання дія обробника
        // ПЕРЕД `PersistChangesAsync`, тож гачок тут ставить чужий коміт рівно
        // між перевіркою версії та записом. Синхронне очікування навмисне:
        // порядок має бути жорстким, інакше «гонка» стала б випадковістю.
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns(_ =>
               {
                   interleave?.Invoke();
                   return (PeriodState?)PeriodState.Open;
               });

        var access = Substitute.For<IAccessDecisionService>();
        var profile = new AccessProfile
        {
            CacheKey = "p", UserId = 1, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        };
        access.BuildProfileAsync(1, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        // ⛔ Рішення на КОЖНУ пару (рядок, колонка) зрізу — саме так поводиться
        // справжній `AccessDecisionService.CanEditSliceAsync` (`:280-295`:
        // подвійний цикл по рядках і колонках, без пропусків). Тут стояв
        // ПОРОЖНІЙ словник, і тест проходив лише тому, що обробник трактував
        // відсутність рішення як дозвіл (`DAT-04`). Предмет цього файлу — не
        // права, тож передумова тепер названа явно, а не отримана з дефекту.
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
        user.UserId.Returns(1);

        // Таблиця без прив'язаної методології — fast-path gate-у обов'язкових
        // вхідних колонок: цей тест про версію рядка, не про методологію.
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
            methodologies, registries, headers, auditWriter, new AuditReader(db), jobs, uow, user, clock, new SheetEditGate(db), new Ecr.Infrastructure.Persistence.UnitCatalog(db));
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int ordinal, CellDataType type)
    {
        var id = doc.ColumnDefIds[ordinal - 1];
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C{ordinal}_{id}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Col{ordinal}" }), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private static string CodeOf(TestDocument doc, int ordinal) => $"C{ordinal}_{doc.ColumnDefIds[ordinal - 1]}";

    private async Task<string> FirstRowKeyAsync(TestDocument doc)
        => await ScalarAsync<string>(
            $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.RowIds[0]}");

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

    private async Task<T?> ScalarOrNullAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    /// <summary>Годинник із фіксованим часом — детермінований <c>ModifiedAt</c>.</summary>
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
