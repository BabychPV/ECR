// tests/Ecr.Api.Tests/RowDefaultsEndToEndTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ B175 E2E (P-0118-34, «A впливає на B»): дефолти рядка шаблону (A) без жодного ручного кроку дають НОВОМУ
/// документу, створеному звичайним шляхом <c>POST /api/v1/documents</c>, статус Running (B) — і в зрізі таблиці, і в
/// фільтрі активності D-425 методології. Синтетичний шаблон на реальному SQL: фіксовані рядки, Lookup-колонка статусу,
/// R1/R2 з <c>cellDefaults {Status: RUNNING}</c>, R3 без нього (аналог S1_6/S2_7 проти S3_9 у T7b_00 Land).
/// </summary>
/// <remarks>
/// Шлях: дефолти записуються штатним <c>PUT …/rows/{code}</c>; документ — <c>POST /documents</c>; зріз —
/// <c>GET /documents/{id}/tables/{instance}</c>; D-425 — <see cref="MethodologyResolver.MatchRowsDetailedAsync"/> над
/// справжніми <see cref="ICellStore"/>/<see cref="IRowStore"/>/<see cref="IMetadataCache"/>/<see cref="IRegistryStore"/>
/// застосунку (підмінені лише правило методології «{}» і рішення фільтра «Running = запис RUNNING»).
/// Мутації: зріз не віддає <c>Defaults</c> — червоний (крок зрізу); <c>ColumnDefaultSet.Complete</c> ігнорує дефолти
/// рядка (<c>Of</c> замість <c>OfRow</c>) — червоний (крок D-425).
/// </remarks>
[Collection("SqlServer")]
public sealed class RowDefaultsEndToEndTests(SqlServerFixture sql)
{
    private const string Password = "Api-B175-RowDefaults-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B175")]
    public async Task Новий_документ_через_POST_бачить_Running_з_дефолту_рядка_у_зрізі_і_у_фільтрі_D425()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var seed = await builder.BuildAsync(rowCount: 3);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        int statusColumnId;
        long running;
        string userName;
        string[] rowKeys;
        await using (var db = builder.CreateContext())
        {
            var registry = new RegistryDef(EcrCode.Create($"B175ST{tag}"), Text("Status"), isTemporal: false);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();
            var entry = new RegistryEntry(registry.Id, EcrCode.Create("RUNNING"), Text("Running"), 1, DateTime.UtcNow);
            db.RegistryEntries.Add(entry);
            db.RegistryEntries.Add(new RegistryEntry(registry.Id, EcrCode.Create("REMOVED"), Text("Removed"), 1, DateTime.UtcNow));
            await db.SaveChangesAsync();
            running = entry.Id;

            var status = new ColumnDef(seed.TableDefId, EcrCode.Create("Status"), Text("Status"), 10, CellDataType.Lookup);
            status.SetLookup(registry.Id);
            db.ColumnDefs.Add(status);
            await db.SaveChangesAsync();
            statusColumnId = status.Id;

            rowKeys = [.. db.RowDefs.AsNoTracking().Where(r => r.TableDefId == seed.TableDefId).OrderBy(r => r.Ordinal).Select(r => r.RowKeyValue)];

            userName = $"b175_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"B175_{tag}"), Text("B175"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var permission in new[] { "Document.View", "Document.Create", "Template.View", "Template.Edit" })
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, seed.ProjectId, GrantLevel.Write, isDeny: false));
            await db.SaveChangesAsync();
        }

        using var app = new EcrApiFactory(sql);
        var client = app.CreateClient();
        using (var login = await client.PostAsJsonAsync(
                   new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }))
        {
            Assert.True(login.IsSuccessStatusCode, $"login {login.StatusCode}: {app.ErrorsText}");
        }

        // A: дефолти рядків штатним шляхом автора шаблону (версія — чернетка будівника).
        foreach (var key in rowKeys[..2])
        {
            using var put = await client.PutAsJsonAsync(
                new Uri($"/api/v1/template-versions/{seed.TemplateVersionId}/tables/{seed.TableDefId}/rows/{key}", UriKind.Relative),
                new
                {
                    labelL10n = new Dictionary<string, string> { ["en"] = key },
                    ordinal = (int?)null,
                    rowKind = "Item",
                    parentRowKey = (string?)null,
                    isReadOnly = false,
                    cellDefaults = new Dictionary<string, string> { ["Status"] = "RUNNING" },
                });
            Assert.True(put.StatusCode == HttpStatusCode.OK, $"PUT row {key}: {put.StatusCode}: {await put.Content.ReadAsStringAsync()}");
        }

        // Новий документ — звичайним шляхом інтерфейсу; жодного кроку «проставити статус».
        using var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new { projectId = seed.ProjectId, sheetDefIds = new[] { seed.SheetDefId } });
        var created = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"{create.StatusCode}: {created}\n{app.ErrorsText}");
        var documentId = JsonDocument.Parse(created).RootElement.GetProperty("documentId").GetInt64();

        var tables = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={seed.PeriodKey.Value}", UriKind.Relative));
        var instanceId = tables.EnumerateArray().Single(t => t.GetProperty("tableDefId").GetInt32() == seed.TableDefId)
            .GetProperty("tableInstanceId").GetInt64();

        // B (зріз): R1/R2 читають Running з дефолту рядка, R3 — без дефолту; запису комірки немає ніде (D-283).
        using var sliceResponse = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{instanceId}", UriKind.Relative));
        var sliceText = await sliceResponse.Content.ReadAsStringAsync();
        Assert.True(sliceResponse.IsSuccessStatusCode, $"{sliceResponse.StatusCode}: {sliceText}\n{app.ErrorsText}");
        var rows = JsonDocument.Parse(sliceText).RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("rowKey").GetString()!);
        foreach (var key in rowKeys[..2])
        {
            Assert.Equal("RUNNING", rows[key].GetProperty("defaults").GetProperty("Status").GetString());
            Assert.False(rows[key].GetProperty("cells").TryGetProperty("Status", out _));
        }

        Assert.True(
            !rows[rowKeys[2]].TryGetProperty("defaults", out var none) || none.ValueKind == JsonValueKind.Null,
            "R3 не має дефолту рядка");

        // B (D-425): методологія з фільтром активності допускає R1/R2 і виключає R3 — на справжніх сховищах.
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var methodology = Substitute.For<IMethodologyStore>();
        methodology.GetRulesAsync(71, Arg.Any<CancellationToken>())
            .Returns([new MethodologyRule(71, EcrCode.Create("RULE"), "{}", priority: 10)]);
        var activity = Substitute.For<IRowActivityFilterSource>();
        activity.ResolveForVersionAsync(71, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new RowActivityFilterResolution(
                running.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Dictionary<int, IReadOnlyList<int>> { [seed.TableDefId] = [statusColumnId] }));
        var resolver = new MethodologyResolver(
            methodology,
            services.GetRequiredService<ICellStore>(),
            services.GetRequiredService<IRowStore>(),
            columnMapper: null,
            rowActivity: activity,
            defaults: new ColumnDefaultValues(
                services.GetRequiredService<IMetadataCache>(), services.GetRequiredService<IRegistryStore>()));

        var outcome = await resolver.MatchRowsDetailedAsync(71, instanceId, CancellationToken.None);

        Assert.Equal(rowKeys[..2], outcome.Matches.Select(m => m.RowKey).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal([rowKeys[2]], outcome.Excluded);
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
