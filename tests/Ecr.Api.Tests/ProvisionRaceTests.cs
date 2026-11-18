// tests/Ecr.Api.Tests/ProvisionRaceTests.cs
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// B167: ідемпотентне розгортання, запущене двічі ОДНОЧАСНО, — та сама відповідь, що й послідовний повтор (успіх), і той самий
/// кінцевий стан, що й одне розгортання. Послідовний повтор кожного з цих шляхів — «нічого не змінено», а не відмова.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>e1 — <c>POST /air-limits/provision</c> (HTTP, зустріч на вході конвеєра, як у B158d): тип AIR, довідник AIR_AREA, профіль
/// ERA_TABLE_33 на ВЛАСНІЙ базі (у спільній AIR уже розгорнуто, і розгортання там нічого не робить).</item>
/// <item>e2 — системне створення дерева НАЕ на старті (<see cref="AirTypeSystemProvisioner.EnsureNaeTreeAsync"/>): два старти
/// інстансів одночасно — дві області DI, бар'єр. Свій код довідника, щоб не чіпати NAE_TREE спільної бази.</item>
/// <item>e3 — той самий старт над НАЯВНИМ деревом: дозаповнення <c>RegionCode</c> вузла REGION (<c>RepairScopeCodesAsync</c>,
/// єдиний запис <c>RegistryValue</c> НАЕ поза HTTP).</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class ProvisionRaceTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private const string Password = "Provision-Race-B167-2099!";

    private static readonly string[] Admin =
    [
        "Registry.View", "Registry.EditDefinition", "Registry.EditData", "Registry.Publish", "Permit.EditType", "Permit.PublishType",
    ];

    // ───────────────────────────── e1: POST /air-limits/provision ─────────────────────────────

    /// <summary>
    /// e1: два <c>POST /air-limits/provision</c> одночасно на базі, де AIR ще не розгорнуто, — обидва <c>200</c> (послідовний
    /// повтор — теж <c>200</c>, <c>typeCreated=false</c>), рівно один тип, одна публікація, один AIR_AREA з двома записами й один
    /// профіль. Доти переможений отримував відмову на AIR_AREA (<c>UQ_RegistryDef</c>), на типі чи на профілі.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B167")]
    public async Task e1_Розгортання_AIR_двічі_одночасно_обидва_200_і_стан_одного_розгортання()
    {
        var database = SqlServerFixture.WithOwnDatabase("_b167prov");
        await database.InitializeAsync();
        try
        {
            var meetings = new ConcurrentDictionary<string, AdminDuplicateRequestHttpTests.Meeting>(StringComparer.Ordinal);
            using var app = new EcrApiFactory(database)
            {
                ExtraServices = s => s.AddSingleton<IStartupFilter>(new AdminDuplicateRequestHttpTests.MeetingStartupFilter(meetings)),
            };

            // Старт без довідника речовин: системне розгортання AIR відмовляє (попередження), дерево НАЕ — створено.
            using var first = await SignedInAsync(app, database);
            using var second = await SignedInAsync(app, database);
            await using (var db = Context(database))
            {
                Assert.False(await db.PermitTypeDefs.AnyAsync(t => t.Code == AirLimitsSchema.TypeCode));
                db.RegistryDefs.Add(new RegistryDef(
                    EcrCode.Create(AirLimitsSchema.DefaultSubstanceRegistry), Text(AirLimitsSchema.DefaultSubstanceRegistry), isTemporal: false));
                await db.SaveChangesAsync();
            }

            async Task<(HttpStatusCode Status, string Body)> ProvisionAsync(HttpClient client, string? meet)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/air-limits/provision", UriKind.Relative));
                if (meet is not null)
                {
                    request.Headers.Add(AdminDuplicateRequestHttpTests.MeetHeader, meet);
                }

                using var response = await client.SendAsync(request);
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            // Одночасно.
            var key = Guid.NewGuid().ToString("N");
            var meeting = meetings.GetOrAdd(key, _ => new AdminDuplicateRequestHttpTests.Meeting());
            var replies = await Task.WhenAll(Task.Run(() => ProvisionAsync(first, key)), Task.Run(() => ProvisionAsync(second, key)));
            foreach (var reply in replies)
            {
                output.WriteLine($"RACE provision {(int)reply.Status} {reply.Body}");
            }

            Assert.True(meeting.Overlapped, "provision: запити не зустрілися на вході конвеєра");
            Assert.True(
                replies.All(r => r.Status == HttpStatusCode.OK),
                $"очікувались обидва 200:\n{(int)replies[0].Status} {replies[0].Body}\n{(int)replies[1].Status} {replies[1].Body}\n{app.ErrorsText}");
            var reports = replies.Select(r => JsonDocument.Parse(r.Body).RootElement.Clone()).ToList();
            var winner = Assert.Single(reports, r => r.GetProperty("typeCreated").GetBoolean());
            var loser = Assert.Single(reports, r => !r.GetProperty("typeCreated").GetBoolean());
            Assert.True(winner.GetProperty("profileCreated").GetBoolean(), winner.ToString());
            Assert.False(loser.GetProperty("profileCreated").GetBoolean(), loser.ToString());
            Assert.True(loser.GetProperty("published").GetBoolean(), loser.ToString());
            Assert.Equal(winner.GetProperty("version").GetInt32(), loser.GetProperty("version").GetInt32());

            // Послідовний повтор — той самий 200 «нічого не змінено», що й у переможеного.
            var repeated = await ProvisionAsync(first, null);
            Assert.True(repeated.Status == HttpStatusCode.OK, $"{(int)repeated.Status} {repeated.Body}\n{app.ErrorsText}");
            var again = JsonDocument.Parse(repeated.Body).RootElement;
            Assert.False(again.GetProperty("typeCreated").GetBoolean());
            Assert.False(again.GetProperty("profileCreated").GetBoolean());
            Assert.Equal(winner.GetProperty("version").GetInt32(), again.GetProperty("version").GetInt32());

            // Кінцевий стан — одне розгортання.
            await using (var db = Context(database))
            {
                var type = await db.PermitTypeDefs.AsNoTracking().SingleAsync(t => t.Code == AirLimitsSchema.TypeCode);
                Assert.Equal(winner.GetProperty("version").GetInt32(), type.Version);
                Assert.Equal(1, await CountAsync(
                    database,
                    "SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = N'cfg.PermitTypeDef' AND Operation = N'PublishPermitType' AND EntityId = @id",
                    type.Id));
                var area = await db.RegistryDefs.AsNoTracking().SingleAsync(d => d.Code == AirLimitsSchema.DefaultAreaRegistry);
                Assert.Equal(2, await db.RegistryEntries.CountAsync(e => e.RegistryDefId == area.Id && !e.IsDeleted));
                Assert.Equal(1, await db.RegistryImportProfiles.CountAsync(p => p.Code == AirLimitsSchema.ProfileCode));
            }
        }
        finally
        {
            await database.DisposeAsync();
            await DropAsync(database);
        }
    }

    // ───────────────────────────── e2: дерево НАЕ на старті ─────────────────────────────

    /// <summary>
    /// e2: два старти інстансів одночасно створюють ВІДСУТНЄ дерево НАЕ як система — обидва без відмови (послідовний повтор —
    /// <c>Created=false</c> без відмови), рівно одне <c>Created=true</c>, один довідник із п'ятьма рівнями. Доти переможений
    /// отримував загальний 409 на <c>UQ_RegistryDef</c> (старт ковтав його попередженням і пропускав розгортання AIR).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B167")]
    public async Task e2_Дерево_НАЕ_на_двох_стартах_одночасно_обидва_без_відмови_і_одне_дерево()
    {
        using var app = new EcrApiFactory(sql);
        _ = app.CreateClient();
        var code = $"NAE_B167_{Guid.NewGuid():N}"[..20].ToUpperInvariant();

        var (results, overlapped) = await RaceAsync(app, code);
        foreach (var result in results)
        {
            output.WriteLine($"RACE nae-create {result}");
        }

        Assert.True(overlapped, "nae-create: виклики не перетнулися в часі");
        Assert.True(results.All(r => r.Error is null), $"відмова на одночасному старті:\n{string.Join("\n", results)}\n{app.ErrorsText}");
        Assert.Single(results, r => r.Created == true);

        // Послідовний повтор — без відмови й без змін.
        var repeated = await EnsureAsync(app, code);
        Assert.True(repeated.Error is null && repeated.Created == false, repeated.ToString());

        await using var db = Context(sql);
        var definition = await db.RegistryDefs.AsNoTracking().SingleAsync(d => d.Code == code);
        Assert.Equal(5, await db.RegistryLevelDefs.CountAsync(l => l.RegistryDefId == definition.Id));
    }

    // ───────────────────────────── e3: RegionCode вузла НАЕ на старті ─────────────────────────────

    /// <summary>
    /// e3: два старти одночасно над наявним деревом, де вузол REGION без власного <c>RegionCode</c>, — обидва без відмови (послідовний
    /// повтор — без відмови й без змін), значення одне, рядок аудиту <c>RepairScopeCodes</c> один. Доти переможений отримував
    /// загальний 409 на <c>UQ_RegistryValue</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B167")]
    public async Task e3_RegionCode_вузла_НАЕ_на_двох_стартах_одночасно_обидва_без_відмови_і_одне_значення()
    {
        using var app = new EcrApiFactory(sql);
        _ = app.CreateClient();
        var code = $"NAE_B167_{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var created = await EnsureAsync(app, code);
        Assert.True(created.Error is null && created.Created == true, created.ToString());

        int registryId;
        long nodeId;
        int fieldId;
        await using (var db = Context(sql))
        {
            var definition = await db.RegistryDefs.Include(d => d.Fields).SingleAsync(d => d.Code == code);
            registryId = definition.Id;
            fieldId = definition.Fields.Single(f => f.Code == NaeTreeSchema.RegionField).Id;
            var region = await db.RegistryLevelDefs.SingleAsync(l => l.RegistryDefId == definition.Id && l.Code == NaeTreeSchema.LevelRegion);
            var node = new RegistryEntry(definition.Id, EcrCode.Create("R_B167"), Text("B167 region"), 0, DateTime.UtcNow);
            node.SetLevel(region.Id);
            db.RegistryEntries.Add(node);
            await db.SaveChangesAsync();
            nodeId = node.Id;
        }

        var (results, overlapped) = await RaceAsync(app, code);
        foreach (var result in results)
        {
            output.WriteLine($"RACE nae-repair {result}");
        }

        Assert.True(overlapped, "nae-repair: виклики не перетнулися в часі");
        Assert.True(results.All(r => r.Error is null), $"відмова на одночасному старті:\n{string.Join("\n", results)}\n{app.ErrorsText}");

        var repeated = await EnsureAsync(app, code);
        Assert.True(repeated.Error is null && repeated.Created == false, repeated.ToString());

        await using (var db = Context(sql))
        {
            Assert.Equal(1, await db.RegistryValues.CountAsync(v => v.RegistryEntryId == nodeId && v.RegistryFieldDefId == fieldId));
        }

        Assert.Equal(1, await CountAsync(
            sql,
            "SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = N'cfg.RegistryDef' AND Operation = N'RepairScopeCodes' AND EntityId = @id",
            registryId));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    /// <summary>Результат одного «старту»: створено чи ні, або відмова (тип і код помилки, без тексту).</summary>
    private sealed record EnsureResult(bool? Created, string? Error, DateTime Started, DateTime Finished)
    {
        public override string ToString() => Error is null ? $"created={Created}" : $"error={Error}";
    }

    /// <summary>Системне створення дерева НАЕ з кодом <paramref name="code"/> у власній області DI — як крок старту.</summary>
    private static async Task<EnsureResult> EnsureAsync(EcrApiFactory app, string code, Task? gate = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<AirLimitsSettings>() with { NaeRegistryCode = code };
        var provisioner = ActivatorUtilities.CreateInstance<AirTypeSystemProvisioner>(scope.ServiceProvider, settings);
        if (gate is not null)
        {
            await gate;
        }

        var started = DateTime.UtcNow;
        try
        {
            var (_, created) = await provisioner.EnsureNaeTreeAsync(CancellationToken.None);
            return new EnsureResult(created, null, started, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is Application.Errors.EcrException or DbUpdateException or Microsoft.Data.SqlClient.SqlException)
        {
            var code2 = ex is Application.Errors.EcrException ecr ? ecr.ErrorCode : ex.GetType().Name;
            return new EnsureResult(null, $"{ex.GetType().Name} {code2}", started, DateTime.UtcNow);
        }
    }

    /// <summary>Два «старти» одночасно: обидві області готові, бар'єр відпускає обидва разом.</summary>
    private static async Task<(EnsureResult[] Results, bool Overlapped)> RaceAsync(EcrApiFactory app, string code)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = Task.Run(() => EnsureAsync(app, code, gate.Task));
        var two = Task.Run(() => EnsureAsync(app, code, gate.Task));
        await Task.Delay(200);
        gate.SetResult();
        var results = await Task.WhenAll(one, two);
        var overlapped = results[0].Started < results[1].Finished && results[1].Started < results[0].Finished;
        return (results, overlapped);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, SqlServerFixture database)
    {
        var name = $"b167_{Guid.NewGuid():N}"[..20];
        await using (var db = Context(database))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Text("B167 provision race"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var permission in Admin)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static async Task<int> CountAsync(SqlServerFixture database, string sqlText, int id)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static EcrDbContext Context(SqlServerFixture database)
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(database.ConnectionString).Options);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static async Task DropAsync(SqlServerFixture database)
    {
        var master = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"IF DB_ID(N'{database.DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{database.DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database.DatabaseName}]; END";
        await command.ExecuteNonQueryAsync();
    }
}
