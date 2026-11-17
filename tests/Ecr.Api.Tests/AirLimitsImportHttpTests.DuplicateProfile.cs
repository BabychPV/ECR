using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>B158 (лінія d, d4): збереження нового профілю імпорту AIR двічі одночасно.</summary>
public sealed partial class AirLimitsImportHttpTests
{
    /// <summary>
    /// d4: <c>PUT /air-limits/import-profiles/{code}</c> без токена (новий профіль) двічі одночасно — один <c>200</c>, другий
    /// та сама відмова <c>ECR-REG-0409 profileCodeTaken</c> з <c>{code}</c>, що й послідовний повтор (раніше — 500 на
    /// <c>UQ_RegistryImportProfile</c>). Тут, а не в <c>AdminDuplicateRequestHttpTests</c>: профілі живуть у реєстрі
    /// джерел розгорнутого типу AIR, який готує <c>PrepareAsync</c> цього класу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B158d")]
    public async Task d4_Новий_профіль_імпорту_двічі_одночасно_одне_200_друге_та_сама_відмова_що_й_послідовно()
    {
        await PrepareAsync();
        var meetings = new ConcurrentDictionary<string, AdminDuplicateRequestHttpTests.Meeting>(StringComparer.Ordinal);
        using var app = new EcrApiFactory(sql)
        {
            ExtraServices = s => s.AddSingleton<IStartupFilter>(new AdminDuplicateRequestHttpTests.MeetingStartupFilter(meetings)),
        };
        using var first = await SignedInAsync(app, Admin);
        using var second = await SignedInAsync(app, Admin);

        var spec = (await ReadAsync(app, await first.GetAsync(new Uri("/api/v1/air-limits/import-profiles/ERA_TABLE_33", UriKind.Relative))))
            .GetProperty("spec");
        object Body() => new { nameL10n = new Dictionary<string, string> { ["en"] = "B158d" }, spec, rowVersion = (string?)null };

        async Task<(HttpStatusCode Status, JsonElement? Error, string Text)> PutAsync(HttpClient client, string code, string? meet)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/air-limits/import-profiles/{code}", UriKind.Relative))
            {
                Content = JsonContent.Create(Body()),
            };
            if (meet is not null)
            {
                request.Headers.Add(AdminDuplicateRequestHttpTests.MeetHeader, meet);
            }

            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, response.IsSuccessStatusCode || !text.StartsWith('{') ? null : JsonDocument.Parse(text).RootElement, text);
        }

        void AssertCodeTaken((HttpStatusCode Status, JsonElement? Error, string Text) reply, HttpStatusCode status, string code)
        {
            Assert.True(reply.Status == status, $"очікувалось {(int)status}: {(int)reply.Status} {reply.Text}\n{app.ErrorsText}");
            Assert.Equal("ECR-REG-0409", reply.Error?.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-REG-0409.profileCodeTaken", reply.Error?.GetProperty("messageKey").GetString());
            Assert.Equal(code, reply.Error?.GetProperty("code").GetString());
        }

        // Послідовно.
        var code = $"B158D_{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var created = await PutAsync(first, code, null);
        var repeated = await PutAsync(first, code, null);

        Assert.True(created.Status == HttpStatusCode.OK, $"{(int)created.Status} {created.Text}\n{app.ErrorsText}");
        Assert.True((int)repeated.Status is >= 400 and < 500, $"{(int)repeated.Status} {repeated.Text}\n{app.ErrorsText}");
        AssertCodeTaken(repeated, repeated.Status, code);

        // Одночасно.
        var raced = $"B158D_{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var key = Guid.NewGuid().ToString("N");
        var meeting = meetings.GetOrAdd(key, _ => new AdminDuplicateRequestHttpTests.Meeting());
        var replies = await Task.WhenAll(Task.Run(() => PutAsync(first, raced, key)), Task.Run(() => PutAsync(second, raced, key)));
        foreach (var reply in replies)
        {
            output.WriteLine($"RACE import-profiles {(int)reply.Status} {reply.Text}");
        }

        Assert.True(meeting.Overlapped, "import-profiles: запити не зустрілися на вході конвеєра");
        Assert.True(replies.All(r => (int)r.Status < 500), $"5xx:\n{replies[0].Text}\n{replies[1].Text}\n{app.ErrorsText}");
        Assert.Single(replies, r => r.Status == HttpStatusCode.OK);
        AssertCodeTaken(replies.Single(r => r.Status != HttpStatusCode.OK), repeated.Status, raced);

        await using var db = new EcrDbContext(Options());
        Assert.Equal(1, await db.RegistryImportProfiles.CountAsync(p => p.Code == raced));
    }
}
