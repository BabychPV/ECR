// tests/Ecr.Api.Tests/PermitRowNaeSourceHttpTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecr.Api.Tests.PermitListsKit;

namespace Ecr.Api.Tests;

/// <summary>
/// Рядок таблиці дозволу посилається на НАЕ лише джерелом (звіт людини про НАЕ, 05.11): пакетне збереження рядків
/// (<c>POST /registries/PRM_…/entries/batch</c>) приймає вузол рівня <c>SRC</c> і відхиляє регіон чи об'єкт помилкою рядка
/// <c>ECR-REG-0422 naeSourceRequired</c> (звіт 200, <c>applied=false</c>, нічого не записано).
/// </summary>
/// <remarks>
/// Дерево НАЕ — власне для тесту (код у <see cref="NaeTreeSettings"/>), щоб не засмічувати спільний <c>NAE_TREE</c> бази тестів.
/// Мутаційний доказ (виконаний локально): у <c>RegistryBatchHandler.CheckPermitNaeSourceAsync</c> умову рівня замінити на
/// <c>false</c> — рядок із регіоном записується (<c>applied=true</c>), тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitRowNaeSourceHttpTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядок_дозволу_приймає_джерело_НАЕ_і_відхиляє_регіон_та_обєкт()
    {
        var tree = await SeedTreeAsync();
        using var app = new EcrApiFactory(sql)
        {
            ExtraServices = s => s.AddSingleton(new NaeTreeSettings(tree.Code, NaeTreeSettings.Default.RegionSourceRegistryCode)),
        };
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var type = $"PNS{_tag}";
        await PublishTypeAsync(client, type, tree.Code, ["A"]);
        var permitId = await CreatePermitAsync(client, type);

        // Регіон і об'єкт — помилка рядка, нічого не записано.
        foreach (var wrong in new[] { tree.Region, tree.Site })
        {
            var rejected = await BatchAsync(client, type, permitId, $"bad{wrong}", wrong);
            Assert.False(rejected.GetProperty("applied").GetBoolean(), rejected.ToString());
            var error = rejected.GetProperty("rows")[0].GetProperty("errors")[0];
            Assert.Equal(
                ("ECR-REG-0422", "err.ECR-REG-0422.naeSourceRequired", "Site"),
                (error.GetProperty("errorCode").GetString(), error.GetProperty("messageKey").GetString(), error.GetProperty("field").GetString()));
        }

        // Джерело — записується.
        var saved = await BatchAsync(client, type, permitId, "good", tree.Source);
        Assert.True(saved.GetProperty("applied").GetBoolean(), saved.ToString());
        Assert.Equal(1, saved.GetProperty("added").GetInt32());
    }

    private async Task<JsonElement> BatchAsync(HttpClient client, string type, long permitId, string name, long site)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/PRM_{type}_Rows/entries/batch?dryRun=false", UriKind.Relative),
            new
            {
                items = new[]
                {
                    new
                    {
                        clientRowId = name, op = "upsert", id = (long?)null, code = (string?)null, baseVersion = (string?)null,
                        values = new Dictionary<string, string?>
                        {
                            ["PERMIT"] = permitId.ToString(CultureInfo.InvariantCulture),
                            ["Name"] = name,
                            ["Site"] = site.ToString(CultureInfo.InvariantCulture),
                        },
                    },
                },
            });
        var body = await Json(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return body;
    }

    private async Task<long> CreatePermitAsync(HttpClient client, string type)
    {
        var permit = await client.PostAsJsonAsync(
            new Uri("/api/v1/permits", UriKind.Relative),
            new { typeCode = type, number = $"PNS-{_tag}", location = "x", issueDate = "2026-01-05", validFrom = "2026-01-10", validTo = "2027-01-10", reportingFlags = new { form300 = false, form301 = false, form302 = false, tox = false } });
        Assert.True(permit.StatusCode == HttpStatusCode.Created, $"{permit.StatusCode}: {await permit.Content.ReadAsStringAsync()}");
        return (await Json(permit)).GetProperty("id").GetInt64();
    }

    /// <summary>Ієрархічний довідник рівнів REGION → SITE → SRC з трьома вузлами.</summary>
    private async Task<(string Code, long Region, long Site, long Source)> SeedTreeAsync()
    {
        await using var db = sql.CreateContext();
        var registry = new RegistryDef(EcrCode.Create($"NAESG{_tag}"), Text($"NAE src guard {_tag}"), isTemporal: false);
        registry.UseHierarchy(true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var levels = new Dictionary<string, RegistryLevelDef>();
        var spec = new[] { "REGION", "SITE", "SRC" };
        for (var i = 0; i < spec.Length; i++)
        {
            levels[spec[i]] = new RegistryLevelDef(registry.Id, i + 1, EcrCode.Create(spec[i]), Text(spec[i]), false);
            db.RegistryLevelDefs.Add(levels[spec[i]]);
        }

        await db.SaveChangesAsync();

        var nodes = new Dictionary<string, RegistryEntry>();
        foreach (var (key, level, parent) in new[] { ("R1", "REGION", (string?)null), ("S1", "SITE", "R1"), ("N1", "SRC", "S1") })
        {
            var entry = new RegistryEntry(registry.Id, EcrCode.Create($"{key}_{_tag}"), Text(key), 0, DateTime.UtcNow);
            entry.SetLevel(levels[level].Id);
            if (parent is not null)
            {
                entry.SetParent(nodes[parent].Id);
            }

            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            nodes[key] = entry;
        }

        return (registry.Code, nodes["R1"].Id, nodes["S1"].Id, nodes["N1"].Id);
    }
}
