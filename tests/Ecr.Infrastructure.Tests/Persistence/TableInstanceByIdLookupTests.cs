using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// O3 (<c>WR-05</c>): пошук екземпляра таблиці за <c>Id</c> з ключем партиції
/// повертає рівно те саме, що й колишній пошук без ключа.
/// </summary>
/// <remarks>
/// ⛔ Прискорення тут — лише план запиту (<c>PeriodKey IN (SELECT … doc.Period)</c>,
/// seek на партицію). Ці тести стережуть інше: що від цього не змінився
/// РЕЗУЛЬТАТ — ні для екземпляра в не першій партиції, ні для пачки з різних
/// партицій, ні для екземпляра, чийого періоду немає в <c>doc.Period</c>
/// (там працює запасний пошук без ключа), ні для неіснуючого <c>Id</c> (404).
/// Що предикат справді стоїть у SQL, стереже <c>PartitionKeyQueryTests</c>.
///
/// ⚠ Періоди — не ті, що архівують/відновлюють <c>ArchiveJobTests</c>
/// (202703/04) і <c>RestoreYearTests</c> (202705, 202709–11): звільнення
/// партиції йде по періоду і про чужі проєкти не знає. 2030 — рік, якого
/// немає ні в чиїх <c>doc.Period</c>, тож видалення свого рядка періоду
/// справді прибирає ключ із множини.
/// </remarks>
[Collection("SqlServer")]
public sealed class TableInstanceByIdLookupTests(SqlServerFixture sql) : IDisposable
{
    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Екземпляри_з_різних_не_перших_партицій_розв_язуються_як_без_ключа()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var april = await builder.BuildAsync(periodKey: 202604, ct: CancellationToken.None);
        var november = await builder.BuildAsync(periodKey: 202611, ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        var resolved = await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(DateTime.UtcNow))
            .ResolveTableInstancesAsync([april.TableInstanceId, november.TableInstanceId], CancellationToken.None);

        foreach (var doc in new[] { april, november })
        {
            var actual = resolved[doc.TableInstanceId];
            Assert.Equal(await UnboundedAsync(db, doc.TableInstanceId), actual);
            Assert.Equal(doc.PeriodKey.Value, actual.PeriodKey);
            Assert.Equal(doc.DocumentId, actual.DocumentId);
        }

        // Шлях із ключем справді знайшов обидва — запасний пошук тут не працював.
        Assert.Equal(
            2,
            await RowStore.TableInstancesByIdQuery(db, [april.TableInstanceId, november.TableInstanceId]).CountAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Період_без_рядка_в_Period_знаходиться_запасним_пошуком()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 203003, ct: CancellationToken.None);

        await ExecuteAsync("DELETE FROM doc.Period WHERE ProjectId = @p;", ("@p", doc.ProjectId));

        await using var db = builder.CreateContext();

        // Передумова: шлях із ключем цей екземпляр НЕ бачить — інакше тест не
        // доводив би, що запасний пошук взагалі потрібен.
        Assert.Equal(0, await RowStore.TableInstancesByIdQuery(db, [doc.TableInstanceId]).CountAsync());

        var resolved = await new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(DateTime.UtcNow))
            .ResolveTableInstanceAsync(doc.TableInstanceId, CancellationToken.None);

        Assert.Equal(await UnboundedAsync(db, doc.TableInstanceId), resolved);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Неіснуючий_екземпляр_лишається_404()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();

        var error = await Assert.ThrowsAsync<NotFoundException>(() =>
            new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new TestClock(DateTime.UtcNow))
                .ResolveTableInstanceAsync(long.MaxValue - 7, CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рішення_зрізу_в_не_першій_партиції_покривають_увесь_зріз()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202611, columnCount: 3, rowCount: 4, ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        var decisions = await Service(db).CanEditSliceAsync(Profile(doc.ProjectId), doc.TableInstanceId, CancellationToken.None);

        // Рішення є рівно для кожної комірки зрізу і в періоді екземпляра —
        // тобто екземпляр із 202611 розв'язаний, а не підмінений чи пропущений.
        // Самі рішення — предмет інших тестів; тут перевіряється лише пошук.
        var expected = doc.RowIds
            .SelectMany(r => doc.ColumnDefIds.Select(c => new CellAddress(doc.PeriodKey, r, c)))
            .ToHashSet();

        Assert.Equal(expected, decisions.Keys.ToHashSet());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рішення_зрізу_для_неіснуючого_екземпляра_лишаються_404()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202611, ct: CancellationToken.None);

        await using var db = builder.CreateContext();

        var error = await Assert.ThrowsAsync<NotFoundException>(() =>
            Service(db).CanEditSliceAsync(Profile(doc.ProjectId), long.MaxValue - 11, CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", error.ErrorCode);
    }

    /// <summary>Колишній запит — за самим <c>Id</c>, без ключа партиції.</summary>
    private static async Task<Application.Ports.TableInstanceRef> UnboundedAsync(EcrDbContext db, long id)
        => await (
                from instance in db.TableInstances.AsNoTracking()
                where instance.Id == id
                join document in db.Documents.AsNoTracking() on instance.DocumentId equals document.Id
                join project in db.Projects.AsNoTracking() on document.ProjectId equals project.Id
                select new Application.Ports.TableInstanceRef(
                    instance.Id, instance.DocumentId, instance.TableDefId,
                    project.TemplateVersionId, instance.PeriodKeyValue))
            .SingleAsync();

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(new DateTime(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc)),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));

    private static AccessProfile Profile(int projectId)
        => new AccessBuilder()
            .Grant(ResourceKind.Project, projectId, GrantLevel.Write)
            .Build();

    private async Task ExecuteAsync(string sqlText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
