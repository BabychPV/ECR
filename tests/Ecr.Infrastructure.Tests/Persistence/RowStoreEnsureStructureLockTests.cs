// tests/Ecr.Infrastructure.Tests/Persistence/RowStoreEnsureStructureLockTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// L6-02 / N1-04 (аудит 2026-10-09, AN-76): <c>RowStore.EnsureTableInstancesAsync</c> — запис структури
/// документа — береться під спільним <c>doc-structure</c> і не вставляє екземпляри таблиць версії,
/// з якої документ уже перенесено.
/// </summary>
/// <remarks>
/// ⛔ Що було. Метод писав без замка й поза транзакцією. Перенос версії шаблону бере
/// <c>doc-structure</c> винятково й міняє склад аркушів документа; екземпляри, вставлені в цьому
/// вікні за старим складом, давали <c>ECR-TMPL-0404</c> і подвоєну структуру.
///
/// ⚠ Два справжні з'єднання. Перенос імітує транзакція «тримача»: виняткове блокування структури через
/// справжній <c>SheetEditGate</c> (ключ ресурсу тест не дублює), потім заміна складу аркушів і коміт.
/// Жодних пауз за годинником: тест чекає на ознаку «стоїть у черзі на замок» в
/// <c>sys.dm_tran_locks</c>, а не на час.
///
/// ⛔ Мутація: прибрати <c>EnterStructureAsync</c> з <c>RowStore.EnsureTableInstancesAsync</c> —
/// перший тест не дістає відмови, другий створює екземпляр таблиці старого складу.
/// </remarks>
[Collection("SqlServer")]
public sealed class RowStoreEnsureStructureLockTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-02")]
    public async Task Заведення_екземплярів_під_виняткове_блокування_структури_відмовляє_409_і_нічого_не_створює()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var (doc, fresh) = await ArrangeAsync(builder);

        await using var holder = builder.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await new SheetEditGate(holder).EnterStructureAsync(doc.DocumentId, exclusive: true, CancellationToken.None);

        await using var db = builder.CreateContext();
        var store = Store(db, TimeSpan.FromMilliseconds(300));

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => store.EnsureTableInstancesAsync(doc.DocumentId, fresh, CancellationToken.None));

        await holderTransaction.RollbackAsync();

        Assert.Equal(ErrorCodes.SheetBusy, error.ErrorCode);
        Assert.Equal("err.ECR-DOC-4091.structureChanging", error.Details!["messageKey"]);
        await using var check = builder.CreateContext();
        Assert.False(await check.TableInstances.AnyAsync(
            t => t.DocumentId == doc.DocumentId && t.PeriodKeyValue == fresh.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-02")]
    public async Task Заведення_екземплярів_чекає_на_перенос_і_створює_лише_таблиці_нового_складу()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var (doc, fresh) = await ArrangeAsync(builder);

        // «Нова версія»: аркуш і таблиця, на які перенос переведе документ.
        int newSheetId;
        int newTableId;
        await using (var prep = builder.CreateContext())
        {
            var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create("NEWSH"), Text("New sheet"), 90);
            prep.SheetDefs.Add(sheet);
            await prep.SaveChangesAsync();

            var table = new TableDef(
                sheet.Id, EcrCode.Create("NEWTB"), Text("New table"), 1,
                TableLayoutKind.MonthsInColumns, TableRowMode.Dynamic);
            prep.TableDefs.Add(table);
            await prep.SaveChangesAsync();

            newSheetId = sheet.Id;
            newTableId = table.Id;
        }

        // ── З'єднання 1: «перенос» — структура під виняткове блокування, склад ще не змінено ──
        await using var holder = builder.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await new SheetEditGate(holder).EnterStructureAsync(doc.DocumentId, exclusive: true, CancellationToken.None);

        // ── З'єднання 2: перше відкриття періоду — стає в чергу на спільне блокування ──
        var ensure = Task.Run(async () =>
        {
            await using var db = builder.CreateContext();
            return await Store(db, SheetEditGatePolicy.Default.LockTimeout)
                .EnsureTableInstancesAsync(doc.DocumentId, fresh, CancellationToken.None);
        });

        var queued = await WaitUntilQueuedOrDoneAsync(ensure);
        Assert.True(queued, "Заведення екземплярів не стало в чергу на блокування структури документа.");

        // Перенос змінює склад аркушів і фіксується; лише тепер заведення дістає блокування.
        await holder.DocumentSheets.Where(s => s.DocumentId == doc.DocumentId).ExecuteDeleteAsync();
        holder.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, newSheetId));
        await holder.SaveChangesAsync();
        await holderTransaction.CommitAsync();

        var created = await ensure.WaitAsync(TimeSpan.FromSeconds(30));

        await using var check = builder.CreateContext();
        var tableDefs = await check.TableInstances.AsNoTracking()
            .Where(t => t.DocumentId == doc.DocumentId && t.PeriodKeyValue == fresh.Value)
            .Select(t => t.TableDefId)
            .ToListAsync();

        // ⛔ Предмет тесту: ЛИШЕ таблиця нового складу. Старий метод прочитав би склад до переносу
        // і створив екземпляр таблиці, якої документ уже не має.
        Assert.Equal(1, created);
        Assert.Equal([newTableId], tableDefs);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-02")]
    public async Task Повторне_відкриття_за_наявними_екземплярами_не_бере_блокування()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var (doc, _) = await ArrangeAsync(builder);

        // Структуру тримають винятково, але період уже відкривали: екземпляри є, писати нічого.
        await using var holder = builder.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync();
        await new SheetEditGate(holder).EnterStructureAsync(doc.DocumentId, exclusive: true, CancellationToken.None);

        await using var db = builder.CreateContext();
        var created = await Store(db, TimeSpan.FromMilliseconds(300))
            .EnsureTableInstancesAsync(doc.DocumentId, doc.PeriodKey, CancellationToken.None);

        await holderTransaction.RollbackAsync();

        // ⚠ Швидкий шлях лишився без замка: GET таблиць наявного документа не платить за перенос.
        Assert.Equal(0, created);
    }

    private RowStore Store(EcrDbContext db, TimeSpan lockTimeout)
        => new(
            db, new BulkCellLoader(sql.ConnectionString, 1000), new FixedClock(Now),
            archive: null, structureGate: new SheetEditGate(db, new SheetEditGatePolicy(lockTimeout)));

    /// <summary>Документ зі складом і ще не відкритий період того самого проєкту.</summary>
    private static async Task<(TestDocument Doc, PeriodKey Fresh)> ArrangeAsync(TestDocumentBuilder builder)
    {
        var doc = await builder.BuildAsync(periodKey: 202601, ct: CancellationToken.None);
        var fresh = new PeriodKey(202602);

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
        db.Periods.Add(new Period(doc.ProjectId, fresh, 2, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)));
        await db.SaveChangesAsync(CancellationToken.None);

        return (doc, fresh);
    }

    /// <summary>
    /// Чекає, доки запит завершиться або хтось стане в чергу на блокування структури документа;
    /// <c>true</c> — друге.
    /// </summary>
    private async Task<bool> WaitUntilQueuedOrDoneAsync(Task work)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (work.IsCompleted)
            {
                return false;
            }

            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT'
                  AND resource_description LIKE '%doc-struc%'
                """;
            if ((int)(await command.ExecuteScalarAsync())! > 0)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class FixedClock(DateTime utcNow) : Domain.Abstractions.IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
