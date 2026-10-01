// tests/Ecr.Api.Tests/SourceEntitiesViewAccessTests.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>D9: GET /api/v1/sources — Integration.View читає без секретів; без прав — 403 з назвою права.</summary>
[Collection("SqlServer")]
public sealed class SourceEntitiesViewAccessTests(SqlServerFixture sql)
{
    private static readonly Uri Sources = new("/api/v1/sources", UriKind.Relative);

    private static readonly string[] Allowed =
    [
        "id", "code", "displayName", "entityPath", "transport", "isActive", "lastRun", "oldestGap",
        "dataSourceId", "dataSourceCode", "onMissingInSource", "validFromAttribute", "validToAttribute",
        "validToInclusive", "registryDefId",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task View_без_Manage_читає_перелік_а_відповідь_не_розкриває_секрет_і_адресу()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Integration.View")
            .ConfigureAwait(true);
        var tag = $"{Guid.NewGuid():N}"[..8];
        var secretName = $"SecretNameMarker{tag}";
        var endpoint = $"https://svcuser:svcpass{tag}@leak-host-{tag}.example.test/piwebapi";
        var entityCode = $"Vw{tag}";

        await using (var db = new EcrDbContext(Options()))
        {
            var source = new DataSource(
                EcrCode.Create($"Vw{tag}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "D9" }),
                ExternalTransport.PiWebApi, endpoint, secretName);
            db.DataSources.Add(source);
            await db.SaveChangesAsync().ConfigureAwait(true);

            var entity = new SourceEntity(source.Id, entityCode, RegistrySourceKind.External);
            entity.Deactivate(); // спільна база: активна сутність без збору — прогалина health-картки
            db.SourceEntities.Add(entity);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var response = await client.GetAsync(Sources).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains(doc.RootElement.EnumerateArray(), e => e.GetProperty("code").GetString() == entityCode);
        Assert.All(
            doc.RootElement.EnumerateArray(),
            e => Assert.All(e.EnumerateObject(), p => Assert.Contains(p.Name, Allowed)));

        Assert.DoesNotContain(secretName, body, StringComparison.Ordinal);
        Assert.DoesNotContain($"leak-host-{tag}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("svcpass", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_Integration_View_і_Manage_перелік_дає_403_з_назвою_права()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.EditData")
            .ConfigureAwait(true);

        var response = await client.GetAsync(Sources).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Integration.View", body, StringComparison.Ordinal);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;
}
