// tests/Ecr.Api.Tests/PiRawDataStand.cs
using System.Globalization;
using System.Net.Http.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Спільний стенд тестів <c>D-463</c>: документ, з'єднання PI, сутність, мапінг на комірку і зібрані точки.
/// </summary>
/// <remarks>
/// ⚠ Точки й прогони кладуться СИРИМ SQL, а не через <c>CollectionStore</c>: читаюча сторона не має залежати
/// від форми запису збору (паралельна лінія D-462 змінює саме його), а набір точок тут — дані, не поведінка.
/// ⚠ Сутність вимкнена: база тестів спільна, і активна сутність без завершеного збору зробила б
/// <c>SourcesHealthCheck</c> «Degraded» для чужих тестів (той самий прийом, що в <c>EntityFieldMapLifecycleTests</c>).
/// </remarks>
internal static class PiRawDataStand
{
    public const string Password = "Pi-Raw-Data-D463-2026!";

    /// <summary>Межі періоду 202601 тестового документа (пояс <c>Asia/Atyrau</c>, +05:00).</summary>
    public static readonly DateTime PeriodFromUtc = new(2025, 12, 31, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>Кінець періоду 202601 (виключно).</summary>
    public static readonly DateTime PeriodToUtc = new(2026, 1, 31, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>Документ + сутність + мапінг на (колонка 2, рядок 1) з агрегацією <c>Sum</c>.</summary>
    public static async Task<Stand> ArrangeAsync(SqlServerFixture sql, int columnCount = 3)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: columnCount, rowCount: 3, ct: CancellationToken.None)
            .ConfigureAwait(false);
        var tag = doc.SheetCode["SHEET".Length..];

        await using var db = builder.CreateContext();
        var unique = $"{Guid.NewGuid():N}"[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"PiV{unique}"), Name("D-463"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entity = new SourceEntity(dataSource.Id, $"Stack{unique}", RegistrySourceKind.External);
        entity.Deactivate();
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var field = $"\\\\AF\\Plant\\Stack{unique}|CO";
        var rowKey = $"R1_{tag}";
        var map = EntityFieldMap.ToColumn(entity.Id, field, doc.ColumnDefIds[1]);
        map.SetMaterialization(rowKey, AggregationKind.Sum);
        db.EntityFieldMaps.Add(map);

        var run = new CollectionRun(entity.Id, PeriodFromUtc, PeriodToUtc, isCatchUp: false, triggeredByUserId: null, PeriodFromUtc);
        db.CollectionRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(false);
        run.Complete("Succeeded", 0, PeriodToUtc, null);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(doc, tag, entity.Id, field, rowKey, map.Id, run.Id);
    }

    /// <summary>Кладе <paramref name="count"/> точок ряду від <paramref name="from"/> з кроком <paramref name="step"/>; значення — номер точки.</summary>
    public static async Task InsertPointsAsync(
        SqlServerFixture sql, int entityId, string path, long runId, DateTime from, TimeSpan step, int count,
        Func<int, string?>? text = null)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        if (text is null)
        {
            await using var bulk = connection.CreateCommand();
            bulk.CommandText = """
                INSERT INTO ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp], ValueNumeric, ValueString, UnitId, Quality, RetrievedAt, CollectionRunId)
                SELECT @e, @p, DATEADD(SECOND, CAST(t.n * @step AS int), @from), t.n, NULL, NULL, N'Good', @from, @run
                FROM (SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1 AS n
                      FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS t;
                """;
            bulk.Parameters.Add("@e", System.Data.SqlDbType.Int).Value = entityId;
            bulk.Parameters.Add("@p", System.Data.SqlDbType.NVarChar, 400).Value = path;
            bulk.Parameters.Add("@from", System.Data.SqlDbType.DateTime2).Value = from;
            bulk.Parameters.Add("@step", System.Data.SqlDbType.BigInt).Value = (long)step.TotalSeconds;
            bulk.Parameters.Add("@count", System.Data.SqlDbType.Int).Value = count;
            bulk.Parameters.Add("@run", System.Data.SqlDbType.BigInt).Value = runId;
            await bulk.ExecuteNonQueryAsync().ConfigureAwait(false);
            return;
        }

        for (var i = 0; i < count; i++)
        {
            await using var one = connection.CreateCommand();
            one.CommandText = """
                INSERT INTO ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp], ValueNumeric, ValueString, UnitId, Quality, RetrievedAt, CollectionRunId)
                VALUES (@e, @p, @t, @v, @s, NULL, @q, @t, @run);
                """;
            var value = text(i);
            one.Parameters.AddWithValue("@e", entityId);
            one.Parameters.Add("@p", System.Data.SqlDbType.NVarChar, 400).Value = path;
            one.Parameters.Add("@t", System.Data.SqlDbType.DateTime2).Value = from + (step * i);
            one.Parameters.Add("@v", System.Data.SqlDbType.Decimal).Value = value is null ? -1.5m * (i + 1) : DBNull.Value;
            one.Parameters.Add("@s", System.Data.SqlDbType.NVarChar, 1000).Value = (object?)value ?? DBNull.Value;
            one.Parameters.Add("@q", System.Data.SqlDbType.NVarChar, 32).Value = i % 2 == 0 ? "Good" : "@Questionable";
            one.Parameters.AddWithValue("@run", runId);
            await one.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Користувач із правами (глобальними або в проєктах) і грантами.</summary>
    /// <param name="sql">Фікстура.</param>
    /// <param name="permissions">Коди прав ролі.</param>
    /// <param name="projectReads">Проєкти, на які дано грант <c>Read</c>.</param>
    /// <param name="denies">Заборони (вид ресурсу, ідентифікатор).</param>
    public static async Task<string> UserAsync(
        SqlServerFixture sql, string[] permissions, int[]? projectReads = null, (ResourceKind Kind, int Id)[]? denies = null)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var name = $"piv_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(EcrCode.Create($"PIV{Guid.NewGuid():N}"[..20]), Name("D-463 test"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        foreach (var projectId in projectReads ?? [])
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Read));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));

        if (denies is { Length: > 0 })
        {
            var denier = new Role(EcrCode.Create($"PIVD{Guid.NewGuid():N}"[..20]), Name("D-463 deny"));
            db.Roles.Add(denier);
            await db.SaveChangesAsync().ConfigureAwait(false);
            db.RoleAssignments.Add(new RoleAssignment(denier.Id, user.Id, null));
            foreach (var (kind, id) in denies)
            {
                db.ResourceGrants.Add(new ResourceGrant(denier.Id, kind, id, GrantLevel.Read, isDeny: true));
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
        return name;
    }

    /// <summary>Клієнт, що увійшов під <paramref name="userName"/>.</summary>
    public static async Task<HttpClient> SignInAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}");
        return client;
    }

    /// <summary>Подія журналу покриття за період.</summary>
    public static async Task AddEventAsync(SqlServerFixture sql, int entityId, int periodKey, string status, string details)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        db.CollectionCoverages.Add(CollectionCoverage.Skipped(entityId, periodKey, status, details, PeriodToUtc));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Успішно покритий інтервал прогону.</summary>
    public static async Task AddCoveredAsync(SqlServerFixture sql, int entityId, long runId, DateTime from, DateTime to)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        db.CollectionCoverages.Add(new CollectionCoverage(entityId, from, to, runId));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    public static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Escape(string value) => Uri.EscapeDataString(value);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Стенд.</summary>
    /// <param name="Doc">Документ.</param>
    /// <param name="Tag">Суфікс кодів документа.</param>
    /// <param name="EntityId">Сутність збору.</param>
    /// <param name="Field">Тег мапінгу.</param>
    /// <param name="RowKey">Рядок, у який цілить мапінг.</param>
    /// <param name="MapId">Мапінг.</param>
    /// <param name="RunId">Прогін збору.</param>
    public sealed record Stand(TestDocument Doc, string Tag, int EntityId, string Field, string RowKey, int MapId, long RunId)
    {
        /// <summary>Колонка, у яку цілить мапінг.</summary>
        public int MappedColumnId => Doc.ColumnDefIds[1];

        /// <summary>Адреса джерела комірки.</summary>
        public string CellSourceUrl(string? rowKey = null, int? columnId = null, long? documentId = null, long? instanceId = null)
            => $"/api/v1/documents/{documentId ?? Doc.DocumentId}/tables/{instanceId ?? Doc.TableInstanceId}/cell-source"
               + $"?rowKey={Escape(rowKey ?? RowKey)}&columnDefId={columnId ?? MappedColumnId}";
    }
}
