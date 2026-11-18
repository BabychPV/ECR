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
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ B175 E2E (P-0118-34, «A впливає на B»): дефолти рядка шаблону (A) без жодного ручного кроку дають НОВОМУ
/// документу, створеному звичайним шляхом <c>POST /api/v1/documents</c>, статус Running (B) — і в зрізі таблиці, і в
/// фільтрі активності D-425 методології. Реальний SQL: фіксована таблиця, Lookup-колонка статусу, рядки й дефолти
/// заводяться штатним <c>PUT …/rows/{code}</c> (тим самим, яким їх пише API-гілка білдера Land).
/// </summary>
/// <remarks>
/// Два сценарії: синтетичний (R1/R2 RUNNING, R3 без) і за МОДЕЛЛЮ Land (рядки T7b_00 і їхні <c>cellDefaults</c> з
/// <c>land-template.model.json</c>, статус-колонка і записи довідника — з моделі; етапу T7bStatus немає).
/// D-425 — <see cref="MethodologyResolver.MatchRowsDetailedAsync"/> над справжніми
/// <see cref="ICellStore"/>/<see cref="IRowStore"/>/<see cref="IMetadataCache"/>/<see cref="IRegistryStore"/> застосунку
/// (підмінені лише правило методології «{}» і рішення фільтра «Running = запис RUNNING»).
/// Мутації: зріз не віддає <c>Defaults</c> — червоні обидва; <c>ColumnDefaultSet.Complete</c> ігнорує дефолти рядка —
/// червоні обидва; у моделі S3_9 з Running або T7b_00 без дефолтів (генератор) — червоний модельний.
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
        var running = new Dictionary<string, string> { ["Status"] = "RUNNING" };
        await RunAsync(
            "Status", ["RUNNING", "REMOVED"],
            [("R1", running), ("R2", running), ("R3", null)],
            expectedAdmitted: ["R1", "R2"], expectedExcluded: ["R3"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B175")]
    public async Task Модель_Land_T7b_00_новий_документ_без_T7bStatus_S1_6_S2_7_допущені_S3_9_виключений()
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SolutionRoot(), "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));
        var root = model.RootElement;
        var t7b = root.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .Single(t => t.GetProperty("code").GetString() == "T7b_00");
        var statusColumn = t7b.GetProperty("statusPresets").GetProperty("column").GetString()!;
        var registryCode = t7b.GetProperty("statusPresets").GetProperty("registry").GetString()!;
        string[] entries =
        [
            .. root.GetProperty("registries").EnumerateArray()
                .Single(r => r.GetProperty("code").GetString() == registryCode)
                .GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("code").GetString()!),
        ];

        var rows = t7b.GetProperty("rows").EnumerateArray()
            .Select(r => (
                Key: r.GetProperty("key").GetString()!,
                Defaults: r.TryGetProperty("cellDefaults", out var d)
                    ? d.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!)
                    : null))
            .ToList();

        await RunAsync(statusColumn, entries, rows, expectedAdmitted: ["S1_6", "S2_7"], expectedExcluded: ["S3_9"]);
    }

    private async Task RunAsync(
        string statusCode,
        string[] entryCodes,
        IReadOnlyList<(string Key, Dictionary<string, string>? Defaults)> templateRows,
        string[] expectedAdmitted,
        string[] expectedExcluded)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var seed = await builder.BuildAsync(rowCount: 0);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        int statusColumnId;
        long running;
        string userName;
        await using (var db = builder.CreateContext())
        {
            var registry = new RegistryDef(EcrCode.Create($"B175ST{tag}"), Text("Status"), isTemporal: false);
            db.RegistryDefs.Add(registry);
            await db.SaveChangesAsync();
            var created = entryCodes
                .Select(c => new RegistryEntry(registry.Id, EcrCode.Create(c), Text(c), 1, DateTime.UtcNow))
                .ToList();
            db.RegistryEntries.AddRange(created);
            await db.SaveChangesAsync();
            running = created.Single(e => e.Code.ToString() == "RUNNING").Id;

            var status = new ColumnDef(seed.TableDefId, EcrCode.Create(statusCode), Text("Status"), 10, CellDataType.Lookup);
            status.SetLookup(registry.Id);
            db.ColumnDefs.Add(status);
            await db.SaveChangesAsync();
            statusColumnId = status.Id;

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

        // A: рядки шаблону з дефолтами — штатним PUT rows автора шаблону (версія — чернетка будівника).
        var ordinal = 0;
        foreach (var (key, defaults) in templateRows)
        {
            var body = new Dictionary<string, object?>
            {
                ["labelL10n"] = new Dictionary<string, string> { ["en"] = key },
                ["ordinal"] = ++ordinal,
                ["rowKind"] = "Item",
                ["parentRowKey"] = null,
                ["isReadOnly"] = false,
            };
            if (defaults is not null)
            {
                body["cellDefaults"] = defaults;
            }

            using var put = await client.PutAsJsonAsync(
                new Uri($"/api/v1/template-versions/{seed.TemplateVersionId}/tables/{seed.TableDefId}/rows/{key}", UriKind.Relative),
                body);
            Assert.True(put.StatusCode == HttpStatusCode.OK, $"PUT row {key}: {put.StatusCode}: {await put.Content.ReadAsStringAsync()}");
        }

        // Новий документ — звичайним шляхом інтерфейсу; жодного кроку «проставити статус» (T7bStatus).
        using var create = await client.PostAsJsonAsync(
            new Uri("/api/v1/documents", UriKind.Relative),
            new { projectId = seed.ProjectId, sheetDefIds = new[] { seed.SheetDefId } });
        var createdText = await create.Content.ReadAsStringAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, $"{create.StatusCode}: {createdText}\n{app.ErrorsText}");
        var documentId = JsonDocument.Parse(createdText).RootElement.GetProperty("documentId").GetInt64();

        var tables = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/documents/{documentId}/tables?periodKey={seed.PeriodKey.Value}", UriKind.Relative));
        var instanceId = tables.EnumerateArray().Single(t => t.GetProperty("tableDefId").GetInt32() == seed.TableDefId)
            .GetProperty("tableInstanceId").GetInt64();

        // B (зріз): допущені рядки читають RUNNING з дефолту рядка; виключені — без дефолту; комірок статусу немає (D-283).
        using var sliceResponse = await client.GetAsync(
            new Uri($"/api/v1/documents/{documentId}/tables/{instanceId}", UriKind.Relative));
        var sliceText = await sliceResponse.Content.ReadAsStringAsync();
        Assert.True(sliceResponse.IsSuccessStatusCode, $"{sliceResponse.StatusCode}: {sliceText}\n{app.ErrorsText}");
        var rows = JsonDocument.Parse(sliceText).RootElement.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("rowKey").GetString()!);
        foreach (var key in expectedAdmitted)
        {
            Assert.Equal("RUNNING", rows[key].GetProperty("defaults").GetProperty(statusCode).GetString());
            Assert.False(rows[key].GetProperty("cells").TryGetProperty(statusCode, out _));
        }

        foreach (var key in expectedExcluded)
        {
            Assert.True(
                !rows[key].TryGetProperty("defaults", out var none) || none.ValueKind == JsonValueKind.Null,
                $"{key} не має дефолту рядка");
        }

        // B (D-425): методологія з фільтром активності допускає рядки з дефолтом Running — на справжніх сховищах.
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

        Assert.Equal(expectedAdmitted, outcome.Matches.Select(m => m.RowKey).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(expectedExcluded, outcome.Excluded.OrderBy(k => k, StringComparer.Ordinal));
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Ecr.sln not found");
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
