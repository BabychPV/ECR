using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Registries.Nae;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecr.Api.Tests.PermitListsKit;

namespace Ecr.Api.Tests;

/// <summary>B158 (лінія d, шлях d4b за правилом 429): перше зіставлення вставки таблиці дозволу двічі одночасно.</summary>
public sealed partial class PermitImportMapHttpTests
{
    /// <summary>
    /// d4b: <c>PUT /permit-tables/{code}/import-mapping</c> без <c>If-Match</c> (першого зіставлення ще немає) двічі
    /// одночасно — один <c>200</c>, другий та сама відмова <c>ECR-REG-0409 importMappingChanged {registryCode}</c>, що й
    /// послідовний повтор без токена; рядок профілю один. Той самий індекс <c>UQ_RegistryImportProfile</c>, що й d4:
    /// арм <c>DuplicateRefusalsAdmin</c> і наявний перехоплювач унікальності в обробнику — два шари.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d4b_Перше_зіставлення_вставки_двічі_одночасно_одне_200_друге_та_сама_відмова_що_й_послідовно()
    {
        var world = await SeedAsync();
        var meetings = new ConcurrentDictionary<string, AdminDuplicateRequestHttpTests.Meeting>(StringComparer.Ordinal);
        using var app = new EcrApiFactory(sql)
        {
            ExtraServices = s =>
            {
                s.AddSingleton(new NaeTreeSettings(world.NaeRegistry, NaeTreeSettings.Default.RegionSourceRegistryCode));
                s.AddSingleton(new AirLimitsSettings(world.NaeRegistry, AirLimitsSettings.Default.SubstanceRegistryCode, world.RegionRegistry, world.AreaRegistry));
                s.AddSingleton<IStartupFilter>(new AdminDuplicateRequestHttpTests.MeetingStartupFilter(meetings));
            },
        };
        using var first = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        using var second = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(first, world);
        var url = new Uri($"/api/v1/permit-tables/{table}/import-mapping", UriKind.Relative);
        var columns = new Dictionary<string, string> { ["NAE number"] = "NAE" };

        async Task<(HttpStatusCode Status, string Text)> PutAsync(HttpClient client, string? meet)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(new { columns }) };
            if (meet is not null)
            {
                request.Headers.Add(AdminDuplicateRequestHttpTests.MeetHeader, meet);
            }

            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        // Одночасно — на ще порожній таблиці.
        var key = Guid.NewGuid().ToString("N");
        var meeting = meetings.GetOrAdd(key, _ => new AdminDuplicateRequestHttpTests.Meeting());
        var replies = await Task.WhenAll(Task.Run(() => PutAsync(first, key)), Task.Run(() => PutAsync(second, key)));
        Assert.True(meeting.Overlapped, "import-mapping: запити не зустрілися на вході конвеєра");

        // Послідовно: повтор без токена, коли зіставлення вже є.
        var repeated = await PutAsync(first, null);
        Assert.True((int)repeated.Status is >= 400 and < 500, $"{(int)repeated.Status} {repeated.Text}\n{app.ErrorsText}");

        Assert.True(replies.All(r => (int)r.Status < 500), $"5xx:\n{replies[0].Text}\n{replies[1].Text}\n{app.ErrorsText}");
        Assert.Single(replies, r => r.Status == HttpStatusCode.OK);
        var loser = replies.Single(r => r.Status != HttpStatusCode.OK);
        Assert.Equal(repeated.Status, loser.Status);

        var expected = JsonDocument.Parse(repeated.Text).RootElement;
        var actual = JsonDocument.Parse(loser.Text).RootElement;
        Assert.Equal("ECR-REG-0409", expected.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-0409.importMappingChanged", expected.GetProperty("messageKey").GetString());
        foreach (var name in new[] { "errorCode", "messageKey", "registryCode" })
        {
            Assert.Equal(expected.GetProperty(name).GetString(), actual.TryGetProperty(name, out var v) ? v.GetString() : null);
        }

        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        Assert.Equal(
            1,
            await db.RegistryImportProfiles.CountAsync(p => p.Code == "PASTE_DEFAULT"
                && db.RegistryDefs.Any(d => d.Id == p.RegistryDefId && d.Code == table)));
    }
}
