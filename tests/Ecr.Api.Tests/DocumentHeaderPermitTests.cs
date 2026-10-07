// tests/Ecr.Api.Tests/DocumentHeaderPermitTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// PS-P1D (D-11): поле шапки <c>Permit</c> (<c>Lookup</c>) не бере запис довідника, що не чинний у
/// звітному вікні проєкту, видалений, вимкнений чи чужий (<c>ECR-HDR-4223</c>).
/// </summary>
/// <remarks>
/// ⛔ Червоні до фіксу: шапка приймала будь-який ІСНУЮЧИЙ запис (протермінований дозвіл лягав у документ). Вікно проєкту фікстури —
/// <c>2026-01-01 … 2026-12-31</c> (<c>TestDocumentBuilder</c>); межа запису — перший НЕчинний день.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentHeaderPermitTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-Permit-2026!";
    private const string PermitCode = "PERMIT";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-11")]
    public async Task Протермінований_дозвіл_у_шапці_відхиляється_422_HDR_4223()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchPermitAsync(client, app, s, s.ExpiredEntryId).ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal("ECR-HDR-4223", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-HDR-4223.entryNotValidInWindow", problem.GetProperty("messageKey").GetString());
        Assert.Null(await StoredPermitAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-11")]
    public async Task Дозвіл_чинний_частину_вікна_приймається()
    {
        // Відрізковий принцип ФВ-5.20: чинний лише до 15 червня — усе одно покриває частину звітного року.
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchPermitAsync(client, app, s, s.PartlyValidEntryId).ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}\n{app.ErrorsText}");
        Assert.Equal(s.PartlyValidEntryId, await StoredPermitAsync(s).ConfigureAwait(true));
    }

    [Theory]
    [InlineData("deleted", "err.ECR-HDR-4223.deletedEntry")]
    [InlineData("inactive", "err.ECR-HDR-4223.inactiveEntry")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-11")]
    public async Task Видалений_чи_вимкнений_запис_у_шапці_відхиляється(string kind, string messageKey)
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PatchPermitAsync(
            client, app, s, kind == "deleted" ? s.DeletedEntryId : s.InactiveEntryId).ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity, app).ConfigureAwait(true);
        Assert.Equal("ECR-HDR-4223", problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-11")]
    public async Task Запис_що_став_нечинним_після_вибору_не_блокує_правку_іншого_поля()
    {
        // Те саме значення — не новий вибір (C7): інакше кожен документ із дозволом, що скінчився,
        // перестав би редагуватись в усьому іншому.
        var s = await ArrangeAsync(initialPermitEntry: ExpiredLabel).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var baseVersion = await VersionAsync(client, app, s.DocumentId).ConfigureAwait(true);

        var response = await client.PatchAsJsonAsync(
            HeaderUri(s.DocumentId),
            new
            {
                fields = new object[]
                {
                    new { code = PermitCode, value = (object?)s.ExpiredEntryId.ToString(CultureInfo.InvariantCulture), isEmpty = false },
                    new { code = "NOTE", value = (object?)"Tengiz", isEmpty = false },
                },
                baseVersion,
            }).ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}\n{app.ErrorsText}");
    }

    private const string ExpiredLabel = "expired";
    private const string PartlyValidLabel = "partly";

    private static Uri HeaderUri(long documentId)
        => new($"/api/v1/documents/{documentId.ToString(CultureInfo.InvariantCulture)}/header", UriKind.Relative);

    private static async Task<HttpResponseMessage> PatchPermitAsync(
        HttpClient client, EcrApiFactory app, Scenario s, long entryId)
    {
        var baseVersion = await VersionAsync(client, app, s.DocumentId).ConfigureAwait(false);
        return await client.PatchAsJsonAsync(
            HeaderUri(s.DocumentId),
            new
            {
                fields = new[]
                {
                    new { code = PermitCode, value = (object?)entryId.ToString(CultureInfo.InvariantCulture), isEmpty = false },
                },
                baseVersion,
            }).ConfigureAwait(false);
    }

    private static async Task<string?> VersionAsync(HttpClient client, EcrApiFactory app, long documentId)
    {
        var response = await client.GetAsync(HeaderUri(documentId)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET header: {response.StatusCode}: {body}\n{app.ErrorsText}");

        return JsonDocument.Parse(body).RootElement.TryGetProperty("version", out var version)
            ? version.GetString()
            : null;
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<long?> StoredPermitAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.DocumentHeaderValues.AsNoTracking()
            .Where(v => v.DocumentId == s.DocumentId && v.HeaderFieldDefId == s.PermitFieldId)
            .Select(v => v.ValueRegistryEntryId)
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>
    /// Документ з <c>Lookup</c>-полем <c>PERMIT</c> і текстовим <c>NOTE</c>; довідник з
    /// протермінованим, частково чинним, видаленим і вимкненим записами.
    /// </summary>
    private async Task<Scenario> ArrangeAsync(string? initialPermitEntry = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var registry = new RegistryDef(
            EcrCode.Create($"PERMIT_{Guid.NewGuid():N}"[..20]), Text("Permits"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Межа ValidTo — ПЕРШИЙ нечинний день (напівінтервал).
        var expired = new RegistryEntry(registry.Id, EcrCode.Create("EXP"), Text("Expired"));
        expired.SetValidity(new DateOnly(2024, 1, 1), new DateOnly(2025, 12, 1));
        var partly = new RegistryEntry(registry.Id, EcrCode.Create("PART"), Text("Partly"));
        partly.SetValidity(new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 15));
        var deleted = new RegistryEntry(registry.Id, EcrCode.Create("DEL"), Text("Deleted"));
        deleted.SoftDelete();
        var inactive = new RegistryEntry(registry.Id, EcrCode.Create("OFF"), Text("Inactive"));
        inactive.Deactivate();
        db.RegistryEntries.AddRange(expired, partly, deleted, inactive);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var permit = new HeaderFieldDef(document.TemplateVersionId, EcrCode.Create(PermitCode), Text("Permit"), 0, CellDataType.Lookup);
        permit.SetLookup(registry.Id);
        var note = new HeaderFieldDef(document.TemplateVersionId, EcrCode.Create("NOTE"), Text("Note"), 1, CellDataType.String);
        db.HeaderFieldDefs.AddRange(permit, note);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (initialPermitEntry is not null)
        {
            var initial = initialPermitEntry == ExpiredLabel ? expired : partly;
            db.DocumentHeaderValues.Add(new DocumentHeaderValue(
                document.DocumentId, permit.Id, new DocumentHeaderValueData { ValueRegistryEntryId = initial.Id }));
        }

        var userName = $"prm_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(EcrCode.Create($"PRM_{Guid.NewGuid():N}"), Text("Header permit"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(
            document.DocumentId, permit.Id, userName,
            expired.Id, partly.Id, deleted.Id, inactive.Id);
    }

    private sealed record Scenario(
        long DocumentId, int PermitFieldId, string UserName,
        long ExpiredEntryId, long PartlyValidEntryId, long DeletedEntryId, long InactiveEntryId);
}
