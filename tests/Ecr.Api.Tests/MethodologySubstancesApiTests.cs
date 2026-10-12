// tests/Ecr.Api.Tests/MethodologySubstancesApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
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
/// <c>GET/PUT …/versions/{vid}/substances</c> (LP-07, <c>calc.MethodologySubstance</c>) на справжньому SQL:
/// набір замість дельти, ідемпотентність, відмови 404/409/422 з ключами каталогу, права, CSRF, клон версії.
/// </summary>
/// <remarks>
/// ⛔ Мутації: прибрати перевірку дубля в <c>SaveMethodologySubstancesHandler.Validate</c> — червоний
/// 422-випадок (UQ дає 500); прибрати <c>RequireNotUsedAsync</c> — червоний 409 «substanceInUse»; прибрати
/// <c>RequireDraft</c> у <c>MethodologyVersion.AddSubstance/RemoveSubstance</c> — червоний 409 draftRequired;
/// зняти <c>scope.RequireAsync</c> у контролері — червоні 403/404.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologySubstancesApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Meth-Substances-2026!";

    private static readonly string[] EditorPermissions = ["Calculation.View", "Calculation.EditFormula"];
    private static readonly string[] ViewerPermissions = ["Calculation.View"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порожній_склад_PUT_ставить_набір_у_порядку_повтор_ідемпотентний_зміна_порядку_й_складу()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var entries = await EntriesAsync(3).ConfigureAwait(true);

        var empty = await ReadAsync(await client.GetAsync(Url(stand)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Empty(empty.GetProperty("items").EnumerateArray());

        var first = await ReadAsync(await PutAsync(client, stand, entries[0].Id, entries[1].Id).ConfigureAwait(true))
            .ConfigureAwait(true);
        var items = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([entries[0].Id, entries[1].Id], items.Select(i => i.GetProperty("substanceEntryId").GetInt64()));
        Assert.Equal([0, 1], items.Select(i => i.GetProperty("ordinal").GetInt32()));
        Assert.Equal([entries[0].Code, entries[1].Code], items.Select(i => i.GetProperty("code").GetString()));

        // Повтор того самого тіла нічого не змінює.
        await ReadAsync(await PutAsync(client, stand, entries[0].Id, entries[1].Id).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(2, await CountAsync(stand.VersionId).ConfigureAwait(true));

        // Набір, а не дельта: нова речовина, зміна порядку, [0] прибрана.
        var second = await ReadAsync(await PutAsync(client, stand, entries[1].Id, entries[2].Id).ConfigureAwait(true))
            .ConfigureAwait(true);
        Assert.Equal(
            [entries[1].Id, entries[2].Id],
            second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("substanceEntryId").GetInt64()));

        var read = await ReadAsync(await client.GetAsync(Url(stand)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(
            [entries[1].Id, entries[2].Id],
            read.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("substanceEntryId").GetInt64()));

        // Порядок — Ordinal: переставлені місцями лишаються тими самими записами.
        var swapped = await ReadAsync(await PutAsync(client, stand, entries[2].Id, entries[1].Id).ConfigureAwait(true))
            .ConfigureAwait(true);
        Assert.Equal(
            [entries[2].Id, entries[1].Id],
            swapped.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("substanceEntryId").GetInt64()));
        Assert.Equal(2, await CountAsync(stand.VersionId).ConfigureAwait(true));

        // Порожній перелік очищує склад.
        await ReadAsync(await PutAsync(client, stand).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(0, await CountAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Склад_читається_шляхом_розрахунку_і_переходить_у_клон_версії()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var entries = await EntriesAsync(2).ConfigureAwait(true);

        await ReadAsync(await PutAsync(client, stand, entries[0].Id, entries[1].Id).ConfigureAwait(true)).ConfigureAwait(true);

        // Те, що бачить рушій: IMethodologyStore.GetSubstancesAsync.
        await using (var db = new EcrDbContext(Options()))
        {
            var seen = await new MethodologyStore(db).GetSubstancesAsync(stand.VersionId, CancellationToken.None)
                .ConfigureAwait(true);
            Assert.Equal([entries[0].Id, entries[1].Id], seen.Select(s => s.SubstanceEntryId));
        }

        var clone = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions", UriKind.Relative),
            new { versionNumber = "2.0", copyFromVersionId = stand.VersionId, level = (int)CalculationLevel.Configuration })
            .ConfigureAwait(true);
        var created = await ReadAsync(clone).ConfigureAwait(true);
        var cloneId = created.GetProperty("id").GetInt32();

        var cloned = await ReadAsync(await client.GetAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions/{cloneId}/substances", UriKind.Relative))
            .ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(
            [entries[0].Id, entries[1].Id],
            cloned.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("substanceEntryId").GetInt64()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дубль_відсутній_запис_видалений_запис_і_відсутній_перелік_дають_422_і_склад_не_змінюється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var entries = await EntriesAsync(3, deleteLast: true).ConfigureAwait(true);

        await ReadAsync(await PutAsync(client, stand, entries[0].Id).ConfigureAwait(true)).ConfigureAwait(true);

        var duplicate = await PutAsync(client, stand, entries[0].Id, entries[1].Id, entries[0].Id).ConfigureAwait(true);
        Assert.Equal(
            "err.ECR-CALC-0422.substanceDuplicate",
            (await ProblemAsync(duplicate, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true))
                .GetProperty("messageKey").GetString());

        var missing = await PutAsync(client, stand, entries[0].Id, 9_000_000_000_001L).ConfigureAwait(true);
        Assert.Equal(
            "err.ECR-CALC-0422.substanceNotFound",
            (await ProblemAsync(missing, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true))
                .GetProperty("messageKey").GetString());

        var deleted = await PutAsync(client, stand, entries[0].Id, entries[2].Id).ConfigureAwait(true);
        Assert.Equal(
            "err.ECR-CALC-0422.substanceNotFound",
            (await ProblemAsync(deleted, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true))
                .GetProperty("messageKey").GetString());

        var absent = await client.PutAsJsonAsync(Url(stand), new { }).ConfigureAwait(true);
        Assert.Equal(
            "err.ECR-CALC-0422.substancesRequired",
            (await ProblemAsync(absent, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true))
                .GetProperty("messageKey").GetString());

        // Жодна відмова не зачепила наявний склад.
        var read = await ReadAsync(await client.GetAsync(Url(stand)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(
            [entries[0].Id],
            read.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("substanceEntryId").GetInt64()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Речовину_зі_звуженою_константою_прибрати_не_можна_а_після_зміни_константи_можна()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var entries = await EntriesAsync(2).ConfigureAwait(true);

        await ReadAsync(await PutAsync(client, stand, entries[0].Id, entries[1].Id).ConfigureAwait(true)).ConfigureAwait(true);

        await using (var db = new EcrDbContext(Options()))
        {
            var version = await db.MethodologyVersions.SingleAsync(v => v.Id == stand.VersionId).ConfigureAwait(true);
            var constant = version.AddTextConstant(EcrCode.Create("EF"), "x", ConstantKind.Text);
            constant.SetScope(null, entries[1].Id);
            db.MethodologyConstants.Add(constant);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var blocked = await PutAsync(client, stand, entries[0].Id).ConfigureAwait(true);
        var problem = await ProblemAsync(blocked, HttpStatusCode.Conflict).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0409.substanceInUse", problem.GetProperty("messageKey").GetString());
        Assert.Contains("EF", problem.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await CountAsync(stand.VersionId).ConfigureAwait(true));

        // Лишити речовину в наборі - можна.
        await ReadAsync(await PutAsync(client, stand, entries[1].Id).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(1, await CountAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Опублікована_версія_не_приймає_PUT_а_склад_лишається()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var entries = await EntriesAsync(2).ConfigureAwait(true);
        var stand = await StandAsync(published: true, substances: [entries[0].Id]).ConfigureAwait(true);

        var put = await PutAsync(client, stand, entries[0].Id, entries[1].Id).ConfigureAwait(true);
        var problem = await ProblemAsync(put, HttpStatusCode.Conflict).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0409.draftRequired", problem.GetProperty("messageKey").GetString());

        var clear = await PutAsync(client, stand).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Conflict, clear.StatusCode);
        Assert.Equal(1, await CountAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Theory]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task PUT_з_чужого_сайту_дає_403_і_склад_не_змінюється(string header, string value)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var entries = await EntriesAsync(1).ConfigureAwait(true);

        using var put = new HttpRequestMessage(HttpMethod.Put, Url(stand))
        {
            Content = JsonContent.Create(new { substanceEntryIds = new[] { entries[0].Id } }),
        };
        put.Headers.Add(header, value);
        using var response = await client.SendAsync(put).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лише_View_читає_але_не_пише_а_чужа_адреса_дає_404()
    {
        using var app = new EcrApiFactory(sql);
        using var viewer = await SignedInAsync(app, ViewerPermissions).ConfigureAwait(true);
        var entries = await EntriesAsync(1).ConfigureAwait(true);
        var stand = await StandAsync(substances: [entries[0].Id]).ConfigureAwait(true);

        var read = await ReadAsync(await viewer.GetAsync(Url(stand)).ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Single(read.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(viewer, stand).ConfigureAwait(true)).StatusCode);
        Assert.Equal(1, await CountAsync(stand.VersionId).ConfigureAwait(true));

        var foreign = await viewer.GetAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId + 1000}/versions/{stand.VersionId}/substances", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    private static Uri Url(Stand stand)
        => new($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/substances", UriKind.Relative);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Stand stand, params long[] ids)
        => client.PutAsJsonAsync(Url(stand), new { substanceEntryIds = ids });

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
        => await ProblemAsync(response, HttpStatusCode.OK).ConfigureAwait(false);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<int> CountAsync(int versionId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.MethodologySubstances.CountAsync(s => s.MethodologyVersionId == versionId).ConfigureAwait(false);
    }

    /// <summary>Довідник повітряних речовин із записами; код запису починається з літери (EcrCode), число - у полі SUBSTANCE_CODE (Land).</summary>
    private async Task<List<RegistryEntry>> EntriesAsync(int count, bool deleteLast = false)
    {
        await using var db = new EcrDbContext(Options());
        var registry = new RegistryDef(
            EcrCode.Create($"SUB{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "LP-07 substances" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var list = new List<RegistryEntry>();
        for (var i = 0; i < count; i++)
        {
            var entry = new RegistryEntry(
                registry.Id,
                EcrCode.Create("E" + (301 + (i * 3)).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new LocalizedText(new Dictionary<string, string> { ["en"] = $"Substance {i}" }));
            list.Add(entry);
        }

        if (deleteLast && list.Count > 0)
        {
            list[^1].SoftDelete();
        }

        db.RegistryEntries.AddRange(list);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return list;
    }

    /// <summary>Методологія з версією; склад і публікація — повз обробники.</summary>
    private async Task<Stand> StandAsync(bool published = false, long[]? substances = null)
    {
        await using var db = new EcrDbContext(Options());
        var now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        var methodology = new Methodology(
            EcrCode.Create($"SB{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "LP-07 substances" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var ordinal = 0;
        foreach (var id in substances ?? [])
        {
            db.MethodologySubstances.Add(version.AddSubstance(id, ordinal++));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        if (published)
        {
            version.Publish(publishedByUserId: 2, "LP-07", new DateOnly(2026, 1, 1), testsPassed: true, now);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new Stand(methodology.Id, version.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string[] permissions)
    {
        var name = $"msub_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Substances test" }));

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

    private sealed record Stand(int MethodologyId, int VersionId);
}
