// tests/Ecr.Api.Tests/RegistryCompositionHttpTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Композиція довідників через справжній контейнер, HTTP і SQL Server (RT-12, <c>ФВ-8.16</c>,
/// <c>D-155</c>, <c>D-157</c>, FEATURE-REGISTRY-TABLES §4.8): каскадне видалення на три рівні
/// (потік → кейс → рядки складу) зі звільненням ключів, <c>Restrict</c>, автоматичний код у CSV.
/// </summary>
/// <remarks>
/// Мутаційні докази (§9.2):
/// <list type="bullet">
/// <item><c>RegistryStore.CountReferencesFromOutsideAsync</c> без <c>!ids.Contains(owner.Id)</c>
/// (частини рахуються посиланнями на батька) → <see cref="Каскад_видаляє_батька_з_частинами"/>
/// отримує 409 замість 204;</item>
/// <item><c>DeleteRegistryEntryHandler</c> без <c>keys.ReleaseAsync(part)</c> →
/// <see cref="Каскад_видаляє_батька_з_частинами"/> бачить живий рядок ключа частини;</item>
/// <item><c>DeleteRegistryEntryHandler.CascadePartsAsync</c> без обходу нижче першого рівня →
/// той самий тест бачить рядки складу невидаленими.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed partial class RegistryCompositionHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Compose-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    [Trait("Requirement", "ФВ-8.4")]
    public async Task Каскад_видаляє_батька_з_частинами()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ParentDeletePolicy.Cascade);

        var stream = await CreateAsync(client, f.Stream, $"S{f.Tag}", new() { ["NAME"] = "1D-2" });
        var winter = await CreateAsync(client, f.Case, $"W{f.Tag}", new() { ["STREAM"] = stream, ["CASE_NAME"] = "370 Winter" });
        var summer = await CreateAsync(client, f.Case, $"U{f.Tag}", new() { ["STREAM"] = stream, ["CASE_NAME"] = "370 Summer" });
        await ImportCompositionAsync(client, f, $"W{f.Tag}", $"U{f.Tag}");

        var parts = await EntryIdsAsync(f.Composition.Id);
        Assert.Equal(4, parts.Count);

        var deleted = await client.DeleteAsync(
            new Uri($"/api/v1/registries/{f.Stream.Code}/entries/{stream}", UriKind.Relative));
        Assert.True(
            deleted.StatusCode == HttpStatusCode.NoContent,
            $"{deleted.StatusCode}: {await deleted.Content.ReadAsStringAsync()}\n{app.ErrorsText}");

        await using var db = new EcrDbContext(Options());
        long[] tree = [stream, winter, summer, .. parts];
        var states = await db.RegistryEntries.AsNoTracking()
            .Where(e => tree.Contains(e.Id))
            .Select(e => new { e.Id, e.IsDeleted })
            .ToListAsync();
        Assert.Equal(tree.Length, states.Count);
        Assert.All(states, s => Assert.True(s.IsDeleted, $"запис {s.Id} не видалено каскадом"));

        // Ключі частин обох рівнів звільнені в тій самій транзакції — новий кейс чи рядок складу з
        // тим самим ключем не отримає 409 від видаленого.
        long[] keyed = [winter, summer, .. parts];
        var keys = await db.RegistryEntryKeys.AsNoTracking()
            .Where(k => keyed.Contains(k.RegistryEntryId))
            .Select(k => new { k.RegistryEntryId, k.IsLive })
            .ToListAsync();
        Assert.Equal(keyed.Length, keys.Count);
        Assert.All(keys, k => Assert.False(k.IsLive, $"ключ запису {k.RegistryEntryId} лишився живим"));

        // Ревізія кожного зачепленого довідника зросла: кеш переліку складу не віддасть видалених.
        var revisions = await db.RegistryDefs.AsNoTracking()
            .Where(d => d.Id == f.Case.Id || d.Id == f.Composition.Id)
            .Select(d => new { d.Id, d.DataRevision })
            .ToListAsync();
        Assert.All(revisions, r => Assert.True(r.DataRevision > f.RevisionAfterSeed[r.Id], $"ревізія довідника {r.Id} не зросла"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Restrict_відмовляє_409_і_не_видаляє_нічого()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ParentDeletePolicy.Restrict);

        var stream = await CreateAsync(client, f.Stream, $"S{f.Tag}", new() { ["NAME"] = "1D-2" });
        var winter = await CreateAsync(client, f.Case, $"W{f.Tag}", new() { ["STREAM"] = stream, ["CASE_NAME"] = "370 Winter" });

        var response = await client.DeleteAsync(
            new Uri($"/api/v1/registries/{f.Stream.Code}/entries/{stream}", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("err.ECR-REG-0409.entryReferenced", problem.GetProperty("messageKey").GetString());
        Assert.Equal(1, problem.GetProperty("referenceKinds").GetProperty("registryValues").GetInt32());

        await using var db = new EcrDbContext(Options());
        Assert.False(await db.RegistryEntries.AnyAsync(e => (e.Id == stream || e.Id == winter) && e.IsDeleted));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Посилання_ззовні_на_частину_зупиняє_каскад()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ParentDeletePolicy.Cascade);

        var stream = await CreateAsync(client, f.Stream, $"S{f.Tag}", new() { ["NAME"] = "1D-2" });
        var winter = await CreateAsync(client, f.Case, $"W{f.Tag}", new() { ["STREAM"] = stream, ["CASE_NAME"] = "370 Winter" });

        // Запис іншого довідника посилається на кейс — видалити потік разом із кейсом означало б
        // мовчки лишити це посилання на запис поза обігом.
        await CreateAsync(client, f.Outside, $"O{f.Tag}", new() { ["CASE_REF"] = winter });

        var response = await client.DeleteAsync(
            new Uri($"/api/v1/registries/{f.Stream.Code}/entries/{stream}", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(1, JsonDocument.Parse(body).RootElement.GetProperty("referenceKinds").GetProperty("registryValues").GetInt32());

        await using var db = new EcrDbContext(Options());
        Assert.False(await db.RegistryEntries.AnyAsync(e => (e.Id == stream || e.Id == winter) && e.IsDeleted));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task CSV_без_стовпця_code_дає_автокоди_а_повтор_оновлює_за_ключем()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ParentDeletePolicy.Cascade);

        var stream = await CreateAsync(client, f.Stream, $"S{f.Tag}", new() { ["NAME"] = "1D-2" });
        await CreateAsync(client, f.Case, $"W{f.Tag}", new() { ["STREAM"] = stream, ["CASE_NAME"] = "370 Winter" });

        var first = await ImportAsync(client, f.Composition.Code, $"CASE,COMPONENT,MOL_PCT\r\nW{f.Tag},CH4,90\r\nW{f.Tag},H2S,10\r\n");
        Assert.Equal((2, 0), (first.GetProperty("added").GetInt32(), first.GetProperty("updated").GetInt32()));

        var codes = await CodesAsync(f.Composition.Id);
        Assert.Equal(2, codes.Count);
        Assert.All(codes, c => Assert.Matches(AutoCode(), c));

        // Той самий файл зі зміненим значенням: рядки знаходить первинний ключ, нових записів немає.
        var second = await ImportAsync(client, f.Composition.Code, $"CASE,COMPONENT,MOL_PCT\r\nW{f.Tag},CH4,89.5\r\nW{f.Tag},H2S,10.5\r\n");
        Assert.Equal((0, 2), (second.GetProperty("added").GetInt32(), second.GetProperty("updated").GetInt32()));
        Assert.Equal(codes, await CodesAsync(f.Composition.Id));

        // Код, якого в довіднику немає, — чужа шкала: помилка рядка, файл не застосовано.
        var foreign = await ImportAsync(client, f.Composition.Code, $"code,CASE,COMPONENT,MOL_PCT\r\nX{f.Tag},W{f.Tag},CO2,1\r\n");
        Assert.False(foreign.GetProperty("applied").GetBoolean());
        Assert.Equal(
            "err.ECR-REG-0422.entryCodeAutomatic",
            Assert.Single(foreign.GetProperty("errors").EnumerateArray()).GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Ручний_запис_без_коду_отримує_автокод()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ParentDeletePolicy.Cascade);

        var stream = await CreateAsync(client, f.Stream, $"S{f.Tag}", new() { ["NAME"] = "1D-2" });
        var winter = await CreateAsync(client, f.Case, $"W{f.Tag}", new() { ["STREAM"] = stream, ["CASE_NAME"] = "370 Winter" });

        var id = await CreateAsync(
            client, f.Composition, string.Empty, new() { ["CASE"] = winter, ["COMPONENT"] = "CH4", ["MOL_PCT"] = 90m });

        await using var db = new EcrDbContext(Options());
        var code = await db.RegistryEntries.AsNoTracking().Where(e => e.Id == id).Select(e => e.Code).SingleAsync();
        Assert.Matches(AutoCode(), code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.16")]
    public async Task Збереження_опису_з_колом_композиції_422()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ParentDeletePolicy.Cascade);

        // Коло в даних: потік — частина рядка складу (повз опис). Збереження опису потоку його
        // бачить і відмовляє 422 з ключем, а не 500 і не мовчки.
        int loopFieldId;
        await using (var db = new EcrDbContext(Options()))
        {
            var loop = new RegistryFieldDef(f.Stream.Id, EcrCode.Create("PART_OF"), Name("Part of"), CellDataType.Lookup, 9);
            loop.Update(Name("Part of"), 9, isRequired: true);
            loop.PointTo(f.Composition.Id);
            loop.ComposeInto(ParentDeletePolicy.Cascade);
            db.RegistryFieldDefs.Add(loop);
            await db.SaveChangesAsync();
            loopFieldId = loop.Id;
        }

        var response = await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{f.Stream.Code}/definition", UriKind.Relative),
            new
            {
                fields = new object[]
                {
                    new { id = f.StreamNameFieldId, code = "NAME", nameL10n = new { values = new Dictionary<string, string> { ["en"] = "Name" } }, dataType = "String", ordinal = 1, isRequired = false, isKey = true, lookupRegistryDefId = (int?)null, unitId = (int?)null },
                    new { id = loopFieldId, code = "PART_OF", nameL10n = new { values = new Dictionary<string, string> { ["en"] = "Part of" } }, dataType = "Lookup", ordinal = 9, isRequired = true, isKey = false, lookupRegistryDefId = (int?)f.Composition.Id, unitId = (int?)null },
                },
                rules = Array.Empty<object>(),
                reason = "RT-12",
            });
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("err.ECR-REG-0422.compositionCycle", problem.GetProperty("messageKey").GetString());
    }

    [GeneratedRegex("^E[0-9]{9}$")]
    private static partial Regex AutoCode();

    private static async Task<long> CreateAsync(
        HttpClient client, Registry registry, string code, Dictionary<string, object?> values)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = registry.Id,
                code,
                display = new { values = new Dictionary<string, string> { ["en"] = code } },
                parentEntryId = (long?)null,
                values,
            });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
    }

    /// <summary>Два рядки складу на кожен кейс — через CSV без стовпця <c>code</c> (автокоди).</summary>
    private static async Task ImportCompositionAsync(HttpClient client, Fixture f, params string[] caseCodes)
    {
        var csv = new StringBuilder("CASE,COMPONENT,MOL_PCT\r\n");
        foreach (var caseCode in caseCodes)
        {
            csv.Append(CultureInfo.InvariantCulture, $"{caseCode},CH4,90\r\n")
               .Append(CultureInfo.InvariantCulture, $"{caseCode},H2S,10\r\n");
        }

        var report = await ImportAsync(client, f.Composition.Code, csv.ToString());
        Assert.True(report.GetProperty("applied").GetBoolean(), report.ToString());
    }

    private static async Task<JsonElement> ImportAsync(HttpClient client, string registryCode, string csv)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "entries.csv");

        var response = await client.PostAsync(
            new Uri($"/api/v1/registries/{registryCode}/entries/import?dryRun=false", UriKind.Relative), content);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<List<long>> EntryIdsAsync(int registryDefId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryEntries.AsNoTracking()
            .Where(e => e.RegistryDefId == registryDefId)
            .OrderBy(e => e.Id)
            .Select(e => e.Id)
            .ToListAsync();
    }

    private async Task<List<string>> CodesAsync(int registryDefId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryEntries.AsNoTracking()
            .Where(e => e.RegistryDefId == registryDefId)
            .OrderBy(e => e.Id)
            .Select(e => e.Code)
            .ToListAsync();
    }

    /// <summary>
    /// Потік → кейс (композиція з <paramref name="casePolicy"/>, ключ STREAM + CASE_NAME) → склад
    /// (композиція <c>Cascade</c>, ключ CASE + COMPONENT, <c>CodeMode = Auto</c>) і сторонній
    /// довідник із посиланням на кейс.
    /// </summary>
    private async Task<Fixture> SeedAsync(ParentDeletePolicy casePolicy)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var stream = new RegistryDef(EcrCode.Create($"CS{tag}"), Name($"Streams {tag}"), isTemporal: false);
        var @case = new RegistryDef(EcrCode.Create($"CC{tag}"), Name($"Cases {tag}"), isTemporal: false);
        var composition = new RegistryDef(EcrCode.Create($"CG{tag}"), Name($"Composition {tag}"), isTemporal: false);
        composition.UseCodeMode(RegistryCodeMode.Auto);
        var outside = new RegistryDef(EcrCode.Create($"CO{tag}"), Name($"Outside {tag}"), isTemporal: false);
        db.RegistryDefs.AddRange(stream, @case, composition, outside);
        await db.SaveChangesAsync();

        var streamName = Field(stream.Id, "NAME", CellDataType.String, 1, required: false);
        streamName.MarkKey(true);

        var caseStream = Field(@case.Id, "STREAM", CellDataType.Lookup, 1, required: true);
        caseStream.PointTo(stream.Id);
        caseStream.ComposeInto(casePolicy);
        var caseName = Field(@case.Id, "CASE_NAME", CellDataType.String, 2, required: true);

        var compositionCase = Field(composition.Id, "CASE", CellDataType.Lookup, 1, required: true);
        compositionCase.PointTo(@case.Id);
        compositionCase.ComposeInto(ParentDeletePolicy.Cascade);
        var component = Field(composition.Id, "COMPONENT", CellDataType.String, 2, required: true);
        var molPct = Field(composition.Id, "MOL_PCT", CellDataType.Decimal, 3, required: true);

        var outsideRef = Field(outside.Id, "CASE_REF", CellDataType.Lookup, 1, required: false);
        outsideRef.PointTo(@case.Id);

        db.RegistryFieldDefs.AddRange(streamName, caseStream, caseName, compositionCase, component, molPct, outsideRef);
        await db.SaveChangesAsync();

        db.RegistryKeyDefs.AddRange(
            new RegistryKeyDef(@case.Id, EcrCode.Create("PK"), Name("PK"), [caseStream, caseName], isPrimary: true, ignoreCase: true, 0, DateTime.UtcNow),
            new RegistryKeyDef(composition.Id, EcrCode.Create("PK"), Name("PK"), [compositionCase, component], isPrimary: true, ignoreCase: true, 0, DateTime.UtcNow));
        await db.SaveChangesAsync();

        return new Fixture(
            tag,
            new Registry(stream.Id, stream.Code),
            new Registry(@case.Id, @case.Code),
            new Registry(composition.Id, composition.Code),
            new Registry(outside.Id, outside.Code),
            streamName.Id,
            new Dictionary<int, int> { [@case.Id] = @case.DataRevision, [composition.Id] = composition.DataRevision });
    }

    private static RegistryFieldDef Field(int registryId, string code, CellDataType type, int ordinal, bool required)
    {
        var field = new RegistryFieldDef(registryId, EcrCode.Create(code), Name(code), type, ordinal);
        field.Update(Name(code), ordinal, required);
        return field;
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із правами на дані й опис довідників.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"regcm_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry composition test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in new[] { "Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish" })
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private sealed record Registry(int Id, string Code);

    private sealed record Fixture(
        string Tag,
        Registry Stream,
        Registry Case,
        Registry Composition,
        Registry Outside,
        int StreamNameFieldId,
        Dictionary<int, int> RevisionAfterSeed);
}
