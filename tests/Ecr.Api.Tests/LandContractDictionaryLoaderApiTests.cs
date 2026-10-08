// tests/Ecr.Api.Tests/LandContractDictionaryLoaderApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Завантаження довідників вкладки «2. Contract» Land (RC15, L2): ті самі виклики API, що робить
/// <c>tools/land/Import-ContractDictionaries.ps1</c>, по закомічених CSV; два прогони — 0 змін.
/// </summary>
/// <remarks>
/// ⚠ Сам PowerShell тест не тримає (CI — Linux, а Skip заборонений honesty-guard): <see cref="Loader"/>
/// відтворює сценарій скрипта викликами тієї ж форми (тіла JSON, <c>If-Match</c>, <c>asOf</c>). Скрипт
/// окремо прогнано на живому стенді; цей тест охороняє КОНТРАКТ викликів, яким скрипт користується
/// (форма <c>display</c>, <c>nameL10n</c> поля, темпоральний довідник без вікон).
/// </remarks>
[Collection("SqlServer")]
public sealed class LandContractDictionaryLoaderApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Land-Contract-2026!";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RC15-L2")]
    public async Task Два_прогони_завантажувача_створюють_179_записів_і_потім_нуль()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
            app, "Registry.View", "Registry.EditDefinition", "Registry.Publish", "Registry.EditData")
            .ConfigureAwait(true);
        var loader = new Loader(client, $"T{Guid.NewGuid():N}"[..8].ToUpperInvariant());

        var first = await loader.RunAsync().ConfigureAwait(true);
        Assert.Equal(7, first.RegistriesCreated);
        Assert.Equal(179, first.EntriesCreated);
        Assert.Equal(0, first.Skipped);

        var second = await loader.RunAsync().ConfigureAwait(true);
        Assert.Equal(0, second.RegistriesCreated);
        Assert.Equal(0, second.EntriesCreated);
        Assert.Equal(0, second.Skipped);
        Assert.Equal(179, second.EntriesExisting);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RC15-L2")]
    public async Task Записи_мають_NAME_і_двомовний_display_а_Permit_темпоральний_без_вікон_чинний()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
            app, "Registry.View", "Registry.EditDefinition", "Registry.Publish", "Registry.EditData")
            .ConfigureAwait(true);
        var loader = new Loader(client, $"T{Guid.NewGuid():N}"[..8].ToUpperInvariant());
        await loader.RunAsync().ConfigureAwait(true);

        // Area: двомовний підпис і повний рядок Excel у NAME.
        var area = await GetEntriesAsync(client, loader.Code("LAND_AREA")).ConfigureAwait(true);
        Assert.Equal(25, area.Count);
        var detail = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/registries/{loader.Code("LAND_AREA")}/entries/{area.First(e => e.Code == "AREA_002").Id}", UriKind.Relative))
            .ConfigureAwait(true);
        var values = detail.GetProperty("displayL10n").GetProperty("values");
        Assert.Equal("Atyrau training center/Data Center", values.GetProperty("en").GetString());
        Assert.False(string.IsNullOrWhiteSpace(values.GetProperty("ru").GetString()));
        Assert.Contains(
            " - ", detail.GetProperty("values").GetProperty("NAME").GetString(), StringComparison.Ordinal);

        // Permit: темпоральний довідник, записи без вікон видно «на сьогодні» (S-4: вікна — пізніше).
        var permit = await GetEntriesAsync(client, loader.Code("LAND_PERMIT")).ConfigureAwait(true);
        Assert.Equal(9, permit.Count);
        Assert.All(permit, e => Assert.Null(e.ValidTo));
        Assert.Contains(permit, e => e.Code == "KZ79UKR00003155_2023_2030");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RC15-L2")]
    public async Task Правка_адміністратора_не_перезаписується_повторним_прогоном()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
            app, "Registry.View", "Registry.EditDefinition", "Registry.Publish", "Registry.EditData")
            .ConfigureAwait(true);
        var loader = new Loader(client, $"T{Guid.NewGuid():N}"[..8].ToUpperInvariant());
        await loader.RunAsync().ConfigureAwait(true);

        var code = loader.Code("LAND_REGION");
        var regions = await GetEntriesAsync(client, code).ConfigureAwait(true);
        var target = regions.First(e => e.Code == "REG_001");
        var def = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative)).ConfigureAwait(true);

        // Адміністратор перейменував запис і змінив NAME.
        var edit = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{code}/entries", UriKind.Relative),
            new
            {
                id = (long?)target.Id,
                registryDefId = def.GetProperty("id").GetInt32(),
                code = "REG_001",
                display = new { values = new Dictionary<string, string> { ["en"] = "Atyrau (edited)" } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?> { ["NAME"] = "Atyrau (edited)" },
            }).ConfigureAwait(true);
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync().ConfigureAwait(true));

        var again = await loader.RunAsync().ConfigureAwait(true);
        Assert.Equal(0, again.EntriesCreated);

        var after = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/registries/{code}/entries/{target.Id}", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal("Atyrau (edited)", after.GetProperty("values").GetProperty("NAME").GetString());
    }

    private static async Task<List<EntryRow>> GetEntriesAsync(HttpClient client, string registryCode)
    {
        var asOf = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var json = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/registries/{registryCode}/entries?asOf={asOf}", UriKind.Relative)).ConfigureAwait(false);

        return json.EnumerateArray()
            .Select(e => new EntryRow(
                e.GetProperty("id").GetInt64(),
                e.GetProperty("code").GetString()!,
                e.TryGetProperty("validTo", out var to) && to.ValueKind == JsonValueKind.String ? to.GetString() : null))
            .ToList();
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"landct_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Land contract loader test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private sealed record EntryRow(long Id, string Code, string? ValidTo);

    private sealed record Summary(int RegistriesCreated, int EntriesCreated, int EntriesExisting, int Skipped);

    /// <summary>Відтворює сценарій <c>Import-ContractDictionaries.ps1</c> (ті самі виклики й тіла).</summary>
    private sealed class Loader(HttpClient client, string tag)
    {
        private static readonly string[][] Dictionaries =
        [
            ["LAND_AREA", "area.csv", "false"], ["LAND_CONTRACTOR", "contractor.csv", "false"],
            ["LAND_REGION", "region.csv", "false"], ["LAND_LOCATION", "location.csv", "false"],
            ["LAND_ONOFFSHORE", "onoffshore.csv", "false"], ["LAND_ACTIVITY", "activity.csv", "false"],
            ["LAND_PERMIT", "permit.csv", "true"],
        ];

        /// <summary>Код довідника цього прогону: префікс тесту, щоб паралельні прогони не стикалися.</summary>
        public string Code(string landCode) => $"{tag}_{landCode}";

        public async Task<Summary> RunAsync()
        {
            int registries = 0, created = 0, existing = 0, skipped = 0;
            var asOf = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

            foreach (var d in Dictionaries)
            {
                var code = Code(d[0]);
                var rows = ReadCsv(d[1]);

                var list = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/registries", UriKind.Relative)).ConfigureAwait(false);
                if (!list.EnumerateArray().Any(r => r.GetProperty("code").GetString() == code))
                {
                    var create = await client.PostAsJsonAsync(
                        new Uri("/api/v1/registries", UriKind.Relative),
                        new { code, nameL10n = new Dictionary<string, string> { ["en"] = code }, isTemporal = d[2] == "true" })
                        .ConfigureAwait(false);
                    Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                    registries++;
                }

                var definition = await client.GetFromJsonAsync<JsonElement>(
                    new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative)).ConfigureAwait(false);
                if (!definition.GetProperty("fields").EnumerateArray().Any(f => f.GetProperty("code").GetString() == "NAME"))
                {
                    using var put = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative))
                    {
                        Content = JsonContent.Create(new
                        {
                            codeMode = definition.TryGetProperty("codeMode", out var cm) && cm.ValueKind != JsonValueKind.Null ? cm.GetString() : null,
                            fields = new[]
                            {
                                new
                                {
                                    id = (int?)null,
                                    code = "NAME",
                                    nameL10n = new { values = new Dictionary<string, string> { ["en"] = "Name", ["ru"] = "Название" } },
                                    dataType = "String",
                                    ordinal = 1,
                                    isRequired = true,
                                    isKey = true,
                                    lookupRegistryDefId = (int?)null,
                                    unitId = (int?)null,
                                },
                            },
                            keys = Array.Empty<object>(),
                            rules = Array.Empty<object>(),
                            reason = "Land contract dictionaries: field NAME",
                        }),
                    };
                    put.Headers.TryAddWithoutValidation("If-Match", definition.GetProperty("definitionVersion").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
                    var saved = await client.SendAsync(put).ConfigureAwait(false);
                    Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync().ConfigureAwait(false));

                    definition = await client.GetFromJsonAsync<JsonElement>(
                        new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative)).ConfigureAwait(false);
                }

                var have = (await client.GetFromJsonAsync<JsonElement>(
                    new Uri($"/api/v1/registries/{code}/entries?asOf={asOf}", UriKind.Relative)).ConfigureAwait(false))
                    .EnumerateArray().Select(e => e.GetProperty("code").GetString()!.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

                foreach (var row in rows)
                {
                    if (have.Contains(row.Code.ToUpperInvariant()))
                    {
                        existing++;
                        continue;
                    }

                    var display = new Dictionary<string, string> { ["en"] = string.IsNullOrEmpty(row.DisplayEn) ? row.Name : row.DisplayEn };
                    if (!string.IsNullOrWhiteSpace(row.DisplayRu))
                    {
                        display["ru"] = row.DisplayRu;
                    }

                    var post = await client.PostAsJsonAsync(
                        new Uri($"/api/v1/registries/{code}/entries", UriKind.Relative),
                        new
                        {
                            id = (long?)null,
                            registryDefId = definition.GetProperty("id").GetInt32(),
                            code = row.Code,
                            display = new { values = display },
                            parentEntryId = (long?)null,
                            values = new Dictionary<string, object?> { ["NAME"] = row.Name },
                        }).ConfigureAwait(false);

                    if (post.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                    {
                        created++;
                    }
                    else if (post.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
                    {
                        skipped++;
                    }
                    else
                    {
                        Assert.Fail($"{code}/{row.Code}: {post.StatusCode} {await post.Content.ReadAsStringAsync().ConfigureAwait(false)}");
                    }
                }
            }

            return new Summary(registries, created, existing, skipped);
        }

        private static List<CsvRow> ReadCsv(string file)
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "Ecr.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            var path = Path.Combine(dir, "docs", "delivery", "reference-data", "land-contract", file);
            var rows = ParseCsv(File.ReadAllText(path, new UTF8Encoding(false, throwOnInvalidBytes: true)));
            return rows.Skip(1).Select(r => new CsvRow(r[0], r[1], r[2], r[3])).ToList();
        }

        private static List<List<string>> ParseCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else if (ch == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        cell.Append(ch);
                    }

                    continue;
                }

                switch (ch)
                {
                    case '"':
                        quoted = true;
                        break;
                    case ',':
                        row.Add(cell.ToString());
                        cell.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        row.Add(cell.ToString());
                        cell.Clear();
                        rows.Add(row);
                        row = [];
                        break;
                    default:
                        cell.Append(ch);
                        break;
                }
            }

            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                rows.Add(row);
            }

            return rows;
        }

        private sealed record CsvRow(string Code, string Name, string DisplayEn, string DisplayRu);
    }
}
