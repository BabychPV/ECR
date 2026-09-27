// tests/Ecr.Infrastructure.Tests/Jobs/MaterializeIntegrationActorTests.cs
using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Матеріалізація PI пише в комірки від імені <c>svc-integration</c> — наскрізно,
/// без HTTP-запиту, як у задачі Quartz.
/// </summary>
/// <remarks>
/// ⛔ P0. Сід заводив <c>svc-integration</c>, але задача не входила в
/// <see cref="JobActorScope"/>: <c>PatchCellsHandler</c> бачив
/// <c>UserId = null</c> і відмовляв <c>ECR-AUTH-0401</c>. Жоден тест цього не
/// бачив — реальний <c>IntegrationCellPatcher</c> із реальним обробником запису
/// не ганявся ніде (решта тестів задачі — на підробленому патчері).
///
/// ⚠ Тому тут усе справжнє й зібране КОНТЕЙНЕРОМ, як у проді:
/// <c>AddEcrApplication</c> + <c>AddEcrInfrastructure</c>, задача береться за
/// маркером <see cref="IMaterializeCollectedDataJob"/> (саме ним її ставить
/// <c>CollectionJob</c>), а <see cref="ICurrentUser"/> — та сама обгортка
/// <see cref="JobAwareCurrentUser"/>, що в <c>Program.cs</c>, поверх
/// користувача «поза запитом» (<see cref="NoHttpRequestUser"/> повторює
/// <c>Ecr.Api.Auth.CurrentUser</c> без <c>HttpContext</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class MaterializeIntegrationActorTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MidJanuary = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-actor")]
    public async Task Задача_без_HTTP_пише_комірку_від_імені_svc_integration_з_походженням_Integration()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(rowMode: TableRowMode.Mixed, ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
            period.TransitionTo(PeriodState.Open, Now);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // ⚠ Адресат — НОВИЙ рядок таблиці `Mixed`, і це обхід, а не задум.
        // Запис у НАЯВНИЙ рядок (типовий випадок: фіксовані рядки заводяться при
        // відкритті періоду, і будь-який другий прогін) сьогодні падає далі за
        // автором — `ECR-ROW-0409` «рядки з такими ключами вже існують»:
        // `IntegrationCellPatcher` шле `BaseVersion = null`, тобто «створити».
        // Це окремий дефект, поза предметом цього тесту.
        var rowKey = $"PI_{Guid.NewGuid():N}"[..20];
        var entityId = await ArrangeMappingAsync(builder, chain, rowKey);
        var svcId = await SvcIntegrationIdAsync();

        // ⚠ Грант заводить ТЕСТ, і це свідомо: предмет тесту — АВТОР запису, а
        // не права. У сіді `svc-integration` без ролей і грантів, і шлях доступу
        // для нього (зона «Аудит»: `AccessDecisionService`) ще не зроблено — без
        // гранта задача за автором падає далі, на `ECR-DOC-0404`. Грант знімається
        // у `finally`: база спільна для всієї колекції.
        var roleId = await GrantSvcWriteAsync(builder, svcId, chain.ProjectId);

        try
        {
            await using var provider = BuildProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                var job = scope.ServiceProvider.GetRequiredService<IMaterializeCollectedDataJob>();

                await job.ExecuteAsync(
                    new MaterializeTask(
                        entityId, chain.ProjectId, chain.DocumentId, chain.TableInstanceId, chain.PeriodKey.Value,
                        new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(2026, 1, 16, 0, 0, 0, DateTimeKind.Utc)),
                    Substitute.For<IJobProgress>(),
                    CancellationToken.None);

                // ⚠ Після задачі scope знову «поза задачею»: автор не протікає в
                // наступну роботу того самого scope.
                Assert.Null(scope.ServiceProvider.GetRequiredService<JobActorScope>().Current);
            }
        }
        finally
        {
            await RevokeAsync(roleId);
        }

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати вхід у scope з `MaterializeCollectedDataJob`
        // → задача падає `ECR-AUTH-0401` «Анонімний запит не може змінювати дані»,
        // комірки немає.
        var value = await ScalarAsync(
            $"SELECT v.ValueNumeric FROM doc.CellValue v JOIN doc.TableRow r ON r.PeriodKey = v.PeriodKey AND r.Id = v.TableRowId "
            + $"WHERE r.TableInstanceId = {chain.TableInstanceId} AND r.RowKey = N'{rowKey}' AND v.ColumnDefId = {chain.ColumnDefIds[1]}");
        Assert.Equal(7m, Assert.IsType<decimal>(value));

        var author = await ScalarAsync(
            $"SELECT TOP 1 CONCAT(ChangedByUserId, N'|', Origin) FROM aud.CellChange "
            + $"WHERE DocumentId = {chain.DocumentId} AND RowKey = N'{rowKey}' AND ColumnDefId = {chain.ColumnDefIds[1]} ORDER BY Id DESC");
        Assert.Equal($"{svcId}|Integration", author);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-actor")]
    public async Task Без_запису_svc_integration_задача_відмовляє_з_кодом_а_не_пише_від_нікого()
    {
        // ⚠ Відсутність запису імітується ВИМКНЕННЯМ у транзакції, яка
        // відкочується: видалити чи вимкнути рядок `sec.User` у спільній
        // тестовій базі означало б зламати сусідні тести. Контекст — без
        // стратегії повторів (як у будівника): з нею EF не дає власної транзакції.
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE sec.[User] SET IsActive = 0 WHERE UserName = N'svc-integration'");

        var scope = new JobActorScope();
        var error = await Assert.ThrowsAsync<Ecr.Application.Errors.NotFoundException>(
            () => new IntegrationActor(db, scope).EnterAsync(CancellationToken.None));

        await tx.RollbackAsync();

        // ⛔ Код, а не мовчазний запис «від нікого»: без цієї відмови задача
        // дійшла б до `PatchCellsHandler` анонімною і впала б `ECR-AUTH-0401`,
        // яка каже «увійдіть» тому, кому входити нікуди.
        Assert.Equal("ECR-SEC-0404", error.ErrorCode);
        Assert.Contains("svc-integration", error.Message, StringComparison.Ordinal);
        Assert.Null(scope.Current);
    }

    /// <summary>Контейнер як у проді: застосунок + інфраструктура + обгортка автора задачі.</summary>
    private ServiceProvider BuildProvider()
    {
        var configuration = Substitute.For<IConfiguration>();
        configuration[Arg.Any<string>()].Returns((string?)null);
        var connectionStrings = Substitute.For<IConfigurationSection>();
        connectionStrings["Ecr"].Returns(sql.ConnectionString);
        configuration.GetSection("ConnectionStrings").Returns(connectionStrings);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrApplication();
        services.AddEcrInfrastructure(configuration);

        // Те саме, що `Program.cs` (F-01): автор задачі поверх користувача запиту.
        services.AddScoped<JobActorScope>();
        services.AddScoped<ICurrentUser>(sp => new JobAwareCurrentUser(
            new NoHttpRequestUser(), sp.GetRequiredService<JobActorScope>()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Сутність із матеріалізованим мапінгом у НОВИЙ рядок таблиці ланцюга і
    /// дві точки посеред січня (3 + 4, згортка <c>Sum</c> = 7).
    /// </summary>
    private static async Task<int> ArrangeMappingAsync(TestDocumentBuilder builder, TestDocument chain, string rowKey)
    {
        await using var db = builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("P0 actor"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync(CancellationToken.None);

        // ⛔ Неактивна навмисно (як у `MaterializeMappingScopeTests`): активна
        // сутність без завершеного збору робить `SourcesHealthCheck` жовтим.
        var entity = new SourceEntity(dataSource.Id, $"Ent{tag}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync(CancellationToken.None);

        var field = $"F_{tag}";
        var map = EntityFieldMap.ToColumn(entity.Id, field, chain.ColumnDefIds[1]);
        map.SetMaterialization(rowKey, AggregationKind.Sum);
        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync(CancellationToken.None);

        var store = new CollectionStore(db, new TestClock(Now));
        var runId = await store.StartRunAsync(
            entity.Id, MidJanuary, MidJanuary.AddHours(2), isCatchUp: false, triggeredByUserId: null, CancellationToken.None);

        await store.UpsertRawPointsAsync(
            runId,
            entity.Id,
            [
                new SourceDataPoint(field, MidJanuary, 3m, null, null, "Good"),
                new SourceDataPoint(field, MidJanuary.AddHours(1), 4m, null, null, "Good"),
            ],
            CancellationToken.None);

        return entity.Id;
    }

    /// <summary>Тестова роль із грантом <c>Write</c> на проєкт, призначена <c>svc-integration</c>.</summary>
    private static async Task<int> GrantSvcWriteAsync(TestDocumentBuilder builder, int svcId, int projectId)
    {
        await using var db = builder.CreateContext();

        var role = new Role(
            EcrCode.Create($"P0SVC{Guid.NewGuid():N}"[..16]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "P0 integration writer (test)" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync(CancellationToken.None);

        db.RoleAssignments.Add(new RoleAssignment(role.Id, svcId, principalSid: null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Write));
        await db.SaveChangesAsync(CancellationToken.None);

        return role.Id;
    }

    private async Task RevokeAsync(int roleId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DELETE FROM sec.ResourceGrant WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.RoleAssignment WHERE RoleId = {roleId}; "
            + $"DELETE FROM sec.Role WHERE Id = {roleId};";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> SvcIntegrationIdAsync()
        => Convert.ToInt32(
            await ScalarAsync($"SELECT Id FROM sec.[User] WHERE UserName = N'{IntegrationActor.UserName}'"),
            System.Globalization.CultureInfo.InvariantCulture);

    private async Task<object?> ScalarAsync(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>
    /// <c>Ecr.Api.Auth.CurrentUser</c> поза HTTP-запитом: анонімний, а
    /// кореляції немає зовсім (там — той самий виняток).
    /// </summary>
    private sealed class NoHttpRequestUser : ICurrentUser
    {
        public int? UserId => null;

        public string? UserName => null;

        public string CorrelationId
            => throw new InvalidOperationException("ICurrentUser використано поза запитом: HttpContext немає.");

        public string Language => "en";

        public IReadOnlyList<string> GroupSids => [];
    }
}
