// tests/Ecr.Api.Tests/PermitImportMapHttpTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ecr.Api.Tests.PermitListsKit;

namespace Ecr.Api.Tests;

/// <summary>
/// Вставка/імпорт рядків таблиці дозволу із зіставленням стовпців (людина 06.11): збережене спільне зіставлення
/// (<c>GET/PUT /permit-tables/{code}/import-mapping</c>, <c>If-Match</c>, право <c>Permit.EditType</c>), значення посилань за
/// кодом чи назвою (<c>POST …/import/lookups</c>) з Region/Area, виведеними з предків джерела в дереві НАЕ, і аркуш
/// <c>.xlsx</c> текстом (<c>POST …/import/workbook</c>). Справжній SQL.
/// </summary>
/// <remarks>
/// Дерево НАЕ, реєстри регіонів і районів - власні для тесту (<see cref="NaeTreeSettings"/>, <see cref="AirLimitsSettings"/>), щоб
/// не засмічувати спільні <c>NAE_TREE</c> / <c>LAND_REGION</c> / <c>AIR_AREA</c> бази тестів.
/// Мутаційні докази (виконані локально, кожну відновлено):
/// <list type="bullet">
/// <item><c>PermitImportLookupHandler.Target.Resolve</c>: <c>all.Count == 1</c> → <c>all.Count &gt;= 1</c> (неоднозначна назва
/// бере перший запис) — червоніє «значення за кодом, назвою; неоднозначне й не джерело».</item>
/// <item><c>PermitImportLookupHandler.DeriveAsync</c>: <c>Pick</c> за <c>found.Count switch</c> → завжди перший кандидат —
/// червоніє «Region/Area з предків джерела».</item>
/// <item><c>PermitImportMappingHandler.SaveAsync</c>: без звірки <c>If-Match</c> — червоніє «застарілий If-Match — 409».</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed partial class PermitImportMapHttpTests(SqlServerFixture sql)
{
    private static readonly string[] FreeText = ["free text"];

    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зіставлення_читається_порожнім_зберігається_з_IfMatch_і_вимагає_Permit_EditType()
    {
        var world = await SeedAsync();
        using var app = App(world);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(admin, world);
        var url = new Uri($"/api/v1/permit-tables/{table}/import-mapping", UriKind.Relative);

        // Нічого не збережено: порожнє зіставлення, стовпець НАЕ і Region/Area, що виводяться з джерела.
        var empty = await Json(await admin.GetAsync(url));
        Assert.Empty(empty.GetProperty("columns").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("rowVersion").ValueKind);
        Assert.Equal(["NAE"], empty.GetProperty("naeColumns").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["REGION", "AREA"], empty.GetProperty("derivable").EnumerateArray().Select(e => e.GetString()));

        // Створення без If-Match; ключ - нормалізований заголовок, код стовпця - як у таблиці.
        var created = await admin.PutAsJsonAsync(url, new { columns = new Dictionary<string, string> { ["  NAE number "] = "nae", ["Source name"] = "NAME" } });
        Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var first = await Json(created);
        var token = first.GetProperty("rowVersion").GetString()!;
        Assert.Equal(
            [("naenumber", "NAE"), ("sourcename", "NAME")],
            first.GetProperty("columns").EnumerateObject().Select(p => (p.Name, p.Value.GetString())).OrderBy(p => p.Name));

        // Інший користувач (лише читання) бачить те саме - зіставлення спільне.
        using var viewer = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");
        var seen = await Json(await viewer.GetAsync(url));
        Assert.Equal("NAE", seen.GetProperty("columns").GetProperty("naenumber").GetString());

        // Без Permit.EditType - 403, навіть з Registry.EditDefinition.
        using var editor = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View", "Registry.EditData", "Registry.EditDefinition");
        var denied = await Put(editor, url, token, new Dictionary<string, string> { ["x"] = "NAME" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        // Заміна з поточним токеном - 200; повтор із тим самим (уже застарілим) - 409.
        var replaced = await Put(admin, url, token, new Dictionary<string, string> { ["Region"] = "REGION" });
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, await replaced.Content.ReadAsStringAsync());
        await AssertErrorAsync(await Put(admin, url, token, new Dictionary<string, string> { ["Area"] = "AREA" }), HttpStatusCode.Conflict, "ECR-REG-0409");
        await AssertErrorAsync(await Put(admin, url, null, new Dictionary<string, string> { ["Area"] = "AREA" }), HttpStatusCode.Conflict, "ECR-REG-0409");

        // Невідомий стовпець і поле композиції PERMIT - 422.
        var current = (await Json(await admin.GetAsync(url))).GetProperty("rowVersion").GetString();
        await AssertErrorAsync(await Put(admin, url, current, new Dictionary<string, string> { ["x"] = "NOPE" }), HttpStatusCode.UnprocessableEntity, "ECR-IMP-0422");
        await AssertErrorAsync(await Put(admin, url, current, new Dictionary<string, string> { ["x"] = "PERMIT" }), HttpStatusCode.UnprocessableEntity, "ECR-IMP-0422");

        // Реєстр, що не є таблицею дозволу - 404.
        await AssertErrorAsync(
            await admin.GetAsync(new Uri($"/api/v1/permit-tables/{world.RegionRegistry}/import-mapping", UriKind.Relative)),
            HttpStatusCode.NotFound,
            "ECR-REG-0404");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Значення_за_кодом_назвою_неоднозначне_й_не_джерело()
    {
        var world = await SeedAsync();
        using var app = App(world);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(client, world);

        var result = await LookupsAsync(client, table, new
        {
            columns = new object[]
            {
                new
                {
                    column = "NAE",
                    values = new[] { $"0036{_tag}", $"N1_{_tag}", $"  boiler{_tag.ToLowerInvariant()} ", $"Котёл{_tag}", $"Twin{_tag}", $"Site1{_tag}", "nothing" },
                },
                new { column = "REGION", values = new[] { $"RATY{_tag}", $"Mangystau{_tag}", $"DUP{_tag}" } },
                new { column = "NAME", values = FreeText },
            },
            derive = false,
        });

        var nae = Values(result, "NAE");
        Assert.Equal(world.N1, nae[$"0036{_tag}"].EntryId);                       // номер НАЕ (NaeCode)
        Assert.Equal(world.N1, nae[$"N1_{_tag}"].EntryId);                        // код запису
        Assert.Equal(world.N1, nae[$"  boiler{_tag.ToLowerInvariant()} "].EntryId); // назва, регістр і пробіли
        Assert.Equal(world.N1, nae[$"Котёл{_tag}"].EntryId);                      // назва іншою мовою
        Assert.Equal((null, "ambiguous"), (nae[$"Twin{_tag}"].EntryId, nae[$"Twin{_tag}"].Reason));
        Assert.Equal((null, "notSource"), (nae[$"Site1{_tag}"].EntryId, nae[$"Site1{_tag}"].Reason));
        Assert.Equal((null, "unknown"), (nae["nothing"].EntryId, nae["nothing"].Reason));

        var region = Values(result, "REGION");
        Assert.Equal(world.RegionAty, region[$"RATY{_tag}"].EntryId);
        Assert.Equal(world.RegionMan, region[$"Mangystau{_tag}"].EntryId);
        Assert.Equal("ambiguous", region[$"DUP{_tag}"].Reason);

        // Текстовий стовпець - не посилання: у відповіді його немає; виведення не просили - порожньо.
        Assert.DoesNotContain(result.GetProperty("columns").EnumerateArray(), c => c.GetProperty("column").GetString() == "NAME");
        Assert.Empty(result.GetProperty("derived").EnumerateArray());
    }

    /// <remarks>
    /// D-456 (06.11): кандидати — те саме правило імпорту, що <c>LookupTargetEntries.ListAsync</c> із профілем. Deny на реєстр цілі
    /// (так само учасник групи <c>PRMG_*</c> під Deny) — наявний код відповідає <c>unknown</c>, рівно як неіснуючий: без оракула
    /// існування. HTTP-шлях Deny на весь реєстр (рівень реєстру, #757 — <c>CanAccess</c> → <c>Hidden</c>); рівень записів — учасник
    /// групи під Deny — доводить <c>PermitImportLookupGroupDenyTests</c> (Application).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Реєстр_під_Deny_відповідає_unknown_як_неіснуючий_код()
    {
        var world = await SeedAsync();
        using var app = App(world);
        using var admin = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(admin, world);

        int regionDefId;
        await using (var db = sql.CreateContext())
        {
            regionDefId = db.RegistryDefs.Single(d => d.Code == EcrCode.Create(world.RegionRegistry)).Id;
        }

        using var denied = await DeniedAsync(app, regionDefId);
        var body = new
        {
            columns = new object[]
            {
                new { column = "NAE", values = new[] { $"0036{_tag}" } },
                new { column = "REGION", values = new[] { $"RATY{_tag}", $"Mangystau{_tag}", "nothing" } },
            },
            derive = false,
        };

        var region = Values(await LookupsAsync(denied, table, body), "REGION");
        Assert.Equal((null, "unknown"), region[$"RATY{_tag}"]);
        Assert.Equal((null, "unknown"), region[$"Mangystau{_tag}"]);
        Assert.Equal(region["nothing"], region[$"RATY{_tag}"]);

        // Контроль: реєстр без Deny — як і раніше; той самий запит адміністратора знаходить регіон.
        Assert.Equal(world.N1, Values(await LookupsAsync(denied, table, body), "NAE")[$"0036{_tag}"].EntryId);
        Assert.Equal(world.RegionAty, Values(await LookupsAsync(admin, table, body), "REGION")[$"RATY{_tag}"].EntryId);
    }

    /// <summary>Користувач з усіма правами тесту і роллю з Deny на реєстр <paramref name="registryDefId"/>.</summary>
    private async Task<HttpClient> DeniedAsync(EcrApiFactory app, int registryDefId)
    {
        const string Password = "Permit-Import-Deny-2026!";
        var name = $"pimdn_{Guid.NewGuid():N}"[..20];
        await using (var db = sql.CreateContext())
        {
            var user = new Ecr.Domain.Entities.Security.User(name, name, AuthProvider.Local);
            user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Ecr.Domain.Entities.Security.Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Import" }));
            var denier = new Ecr.Domain.Entities.Security.Role(EcrCode.Create($"D{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Deny" }));
            db.Roles.AddRange(role, denier);
            await db.SaveChangesAsync();

            foreach (var permission in AllRights)
            {
                db.RolePermissions.Add(new Ecr.Domain.Entities.Security.RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new Ecr.Domain.Entities.Security.RoleAssignment(role.Id, user.Id, principalSid: null));
            db.ResourceGrants.Add(new Ecr.Domain.Entities.Security.ResourceGrant(denier.Id, ResourceKind.Registry, registryDefId, GrantLevel.Read, isDeny: true));
            db.RoleAssignments.Add(new Ecr.Domain.Entities.Security.RoleAssignment(denier.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Region_і_Area_виводяться_з_предків_джерела_в_дереві_НАЕ()
    {
        var world = await SeedAsync();
        using var app = App(world);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(client, world);

        var result = await LookupsAsync(client, table, new
        {
            columns = new[] { new { column = "NAE", values = new[] { $"0036{_tag}", $"N2_{_tag}", $"N3_{_tag}", $"N4_{_tag}" } } },
            derive = true,
        });

        var derived = result.GetProperty("derived").EnumerateArray().ToDictionary(
            d => d.GetProperty("naeEntryId").GetInt64(),
            d => d.GetProperty("cells").EnumerateArray().ToDictionary(
                c => c.GetProperty("column").GetString()!,
                c => (Id: c.GetProperty("entryId").ValueKind == JsonValueKind.Null ? (long?)null : c.GetProperty("entryId").GetInt64(),
                      Reason: c.GetProperty("reason").GetString())));

        // N1: REGION-вузол (RegionCode) → запис регіону за назвою; AREA-вузол → запис району «… complex».
        Assert.Equal((world.RegionAty, (string?)null), derived[world.N1]["REGION"]);
        Assert.Equal((world.AreaOff, (string?)null), derived[world.N1]["AREA"]);
        // N2: регіон без районів (код вузла R_<код>, назва = назва запису) - району немає, не помилка.
        Assert.Equal((world.RegionMan, (string?)null), derived[world.N2]["REGION"]);
        Assert.False(derived[world.N2].ContainsKey("AREA"));
        // N3: регіону відповідають два записи - неоднозначно, нічого не вгадується.
        Assert.Equal(((long?)null, "ambiguous"), derived[world.N3]["REGION"]);
        // N4: джерело поза деревом груп і без RegionCode - регіону немає.
        Assert.Equal(((long?)null, "noScope"), derived[world.N4]["REGION"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Аркуш_xlsx_текстом_з_вибором_аркуша_і_правом_запису()
    {
        var world = await SeedAsync();
        using var app = App(world);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, AllRights);
        var table = await PublishAsync(client, world);
        var book = Workbook();

        // Без назви аркуша - перший видимий; перелік видимих (прихований не показано).
        var first = await Json(await Upload(client, table, book, null));
        Assert.Equal("Notes", first.GetProperty("sheet").GetString());
        Assert.Equal(["Notes", "Sources"], first.GetProperty("sheets").EnumerateArray().Select(s => s.GetString()));

        // Обраний аркуш: числа інваріантно, дата ISO, логічне true/false, порожні праворуч відкинуто.
        var sources = await Json(await Upload(client, table, book, "Sources"));
        Assert.Equal(
            [["Source name", "NAE number", "Height, m", "Since", "Active"], ["Stack 1", "0036", "12.5", "2026-03-01", "true"], ["Stack 2", "", "7"]],
            sources.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()).ToArray()).ToArray());
        Assert.False(sources.GetProperty("truncated").GetBoolean());

        // Книга e2e-фікстури (`e2e/fixtures/permit-import-two-sheets.xlsx`, рукописний OOXML) читається так само.
        var fixture = await Json(await Upload(client, table, await File.ReadAllBytesAsync(FixturePath()), "Sources"));
        Assert.Equal(
            [["NAE number", "Source name", "Height, m"], ["E2EIMX1", "Xlsx stack", "12.5"]],
            fixture.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()).ToArray()).ToArray());
        Assert.Equal(["Readme", "Sources"], fixture.GetProperty("sheets").EnumerateArray().Select(s => s.GetString()));

        await AssertErrorAsync(await Upload(client, table, book, "Missing"), HttpStatusCode.UnprocessableEntity, "ECR-IMP-0422");
        await AssertErrorAsync(await Upload(client, table, [1, 2, 3], null), HttpStatusCode.UnprocessableEntity, "ECR-IMP-0422");

        // Лише читання - 403: книга читається лише тим, хто може додати рядки.
        using var viewer = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View");
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(viewer, table, book, null)).StatusCode);
    }

    private EcrApiFactory App(World world)
        => new(sql)
        {
            ExtraServices = s =>
            {
                s.AddSingleton(new NaeTreeSettings(world.NaeRegistry, NaeTreeSettings.Default.RegionSourceRegistryCode));
                s.AddSingleton(new AirLimitsSettings(world.NaeRegistry, AirLimitsSettings.Default.SubstanceRegistryCode, world.RegionRegistry, world.AreaRegistry));
            },
        };

    private static async Task<HttpResponseMessage> Put(HttpClient client, Uri url, string? token, Dictionary<string, string> columns)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(new { columns }) };
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{token}\"");
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> LookupsAsync(HttpClient client, string table, object body)
    {
        var response = await client.PostAsJsonAsync(new Uri($"/api/v1/permit-tables/{table}/import/lookups", UriKind.Relative), body);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await Json(response);
    }

    private static Dictionary<string, (long? EntryId, string? Reason)> Values(JsonElement result, string column)
        => result.GetProperty("columns").EnumerateArray()
            .Single(c => c.GetProperty("column").GetString() == column)
            .GetProperty("values").EnumerateArray()
            .ToDictionary(
                v => v.GetProperty("text").GetString()!,
                v => (v.GetProperty("entryId").ValueKind == JsonValueKind.Null ? (long?)null : v.GetProperty("entryId").GetInt64(),
                      v.GetProperty("reason").GetString()));

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string table, byte[] book, string? sheet)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(book);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", "rows.xlsx");
        if (sheet is not null)
        {
            form.Add(new StringContent(sheet), "sheet");
        }

        return await client.PostAsync(new Uri($"/api/v1/permit-tables/{table}/import/workbook", UriKind.Relative), form);
    }

    private static string FixturePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "src", "Ecr.Web", "e2e", "fixtures", "permit-import-two-sheets.xlsx");
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("e2e/fixtures/permit-import-two-sheets.xlsx is not found above the test output folder.");
    }

    private static byte[] Workbook()
    {
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("Notes").Cell(1, 1).Value = "Read me";
        var hidden = workbook.Worksheets.Add("Hidden");
        hidden.Cell(1, 1).Value = "x";
        hidden.Visibility = XLWorksheetVisibility.Hidden;
        var sheet = workbook.Worksheets.Add("Sources");
        string[] header = ["Source name", "NAE number", "Height, m", "Since", "Active"];
        for (var i = 0; i < header.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = header[i];
        }

        sheet.Cell(2, 1).Value = "Stack 1";
        sheet.Cell(2, 2).Value = "0036";
        sheet.Cell(2, 3).Value = 12.5;
        sheet.Cell(2, 4).Value = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Unspecified);
        sheet.Cell(2, 5).Value = true;
        sheet.Cell(3, 1).Value = "Stack 2";
        sheet.Cell(3, 3).Value = 7;
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Тип з таблицею Sources: NAE (дерево тесту), REGION (обов'язковий), AREA, NAME; повертає код реєстру таблиці.</summary>
    private async Task<string> PublishAsync(HttpClient client, World world)
    {
        var code = $"PIM{_tag}";
        var create = await client.PostAsJsonAsync(new Uri("/api/v1/permit-types", UriKind.Relative), new { code, nameL10n = new { en = code }, category = "Air" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var rowVersion = (await Json(create)).GetProperty("rowVersion").GetString()!;

        static object Column(string c, string type, bool required, bool key, string? registry, int order)
            => new
            {
                code = c, nameL10n = new { en = c }, dataType = type, unitId = (int?)null, required, key, group = (string?)null,
                precision = (int?)null, scale = (int?)null, listScope = registry is null ? null : "SharedRegistry", listRegistryCode = registry,
                siblingTableCode = (string?)null, choices = (string[]?)null, order,
            };

        var columns = new[]
        {
            Column("NAE", "Lookup", required: true, key: true, world.NaeRegistry, 1),
            Column("REGION", "Lookup", required: true, key: false, world.RegionRegistry, 2),
            Column("AREA", "Lookup", required: false, key: false, world.AreaRegistry, 3),
            Column("NAME", "String", required: true, key: false, null, 4),
        };
        var tables = new[] { new { code = "Sources", nameL10n = new { en = "Sources (NAE)" }, order = 1, columns, rules = Array.Empty<object>() } };
        var replaced = await client.PutAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}", UriKind.Relative),
            new { rowVersion, nameL10n = new { en = code }, category = "Air", tables, rules = Array.Empty<object>() });
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, $"{replaced.StatusCode}: {await replaced.Content.ReadAsStringAsync()}");
        var fresh = (await Json(replaced)).GetProperty("rowVersion").GetString()!;
        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/permit-types/{code}/publish", UriKind.Relative), new { reason = "тип для зіставлення імпорту", rowVersion = fresh });
        Assert.True(published.StatusCode == HttpStatusCode.OK, $"{published.StatusCode}: {await published.Content.ReadAsStringAsync()}");
        return $"PRM_{code}_Sources";
    }

    private sealed record World(
        string NaeRegistry, string RegionRegistry, string AreaRegistry,
        long N1, long N2, long N3, long N4, long RegionAty, long RegionMan, long AreaOff);

    /// <summary>
    /// Дерево REGION → AREA → SITE → SRC і реєстри регіонів/районів:
    /// R1 (RegionCode ATY…) → A1 (AreaCode OFF…) → S1 → N1 (NaeCode 0036…, «Boiler…»/«Котёл…»), Twin×2;
    /// R2 (код R_MAN…, «Mangystau…», без районів) → S2 → N2; R3 (RegionCode DUP…) → N3; N4 без предків і RegionCode.
    /// </summary>
    private async Task<World> SeedAsync()
    {
        await using var db = sql.CreateContext();
        var tree = new RegistryDef(EcrCode.Create($"NAEIM{_tag}"), Text($"NAE import map {_tag}"), isTemporal: false);
        tree.UseHierarchy(true);
        var regions = new RegistryDef(EcrCode.Create($"REGIM{_tag}"), Text($"Regions {_tag}"), isTemporal: false);
        var areas = new RegistryDef(EcrCode.Create($"AREAIM{_tag}"), Text($"Areas {_tag}"), isTemporal: false);
        db.RegistryDefs.AddRange(tree, regions, areas);
        await db.SaveChangesAsync();

        var fields = new Dictionary<string, RegistryFieldDef>();
        foreach (var (name, ordinal) in new[] { (NaeTreeSchema.NaeCodeField, 1), (NaeTreeSchema.RegionField, 2), (NaeTreeSchema.AreaField, 3) })
        {
            fields[name] = new RegistryFieldDef(tree.Id, EcrCode.Create(name), Text(name), CellDataType.String, ordinal);
            db.RegistryFieldDefs.Add(fields[name]);
        }

        var levels = new Dictionary<string, RegistryLevelDef>();
        string[] spec = [NaeTreeSchema.LevelRegion, NaeTreeSchema.LevelArea, NaeTreeSchema.LevelSite, NaeTreeSchema.LevelSource];
        for (var i = 0; i < spec.Length; i++)
        {
            levels[spec[i]] = new RegistryLevelDef(tree.Id, i + 1, EcrCode.Create(spec[i]), Text(spec[i]), spec[i] == NaeTreeSchema.LevelArea);
            db.RegistryLevelDefs.Add(levels[spec[i]]);
        }

        await db.SaveChangesAsync();

        var nodes = new Dictionary<string, RegistryEntry>();
        async Task<RegistryEntry> Node(string key, string code, LocalizedText display, string level, string? parent, params (string Field, string Value)[] values)
        {
            var entry = new RegistryEntry(tree.Id, EcrCode.Create(code), display, 0, DateTime.UtcNow);
            entry.SetLevel(levels[level].Id);
            if (parent is not null)
            {
                entry.SetParent(nodes[parent].Id);
            }

            db.RegistryEntries.Add(entry);
            await db.SaveChangesAsync();
            foreach (var (field, value) in values)
            {
                var stored = new RegistryValue(entry.Id, fields[field].Id);
                stored.Set(CellDataType.String, value, null);
                db.RegistryValues.Add(stored);
            }

            await db.SaveChangesAsync();
            nodes[key] = entry;
            return entry;
        }

        var t = _tag;
        await Node("R1", $"R1_{t}", Text($"Atyrau region {t}"), NaeTreeSchema.LevelRegion, null, (NaeTreeSchema.RegionField, $"ATY{t}"));
        await Node("A1", $"A1_{t}", Text($"Offshore {t}"), NaeTreeSchema.LevelArea, "R1", (NaeTreeSchema.AreaField, $"OFF{t}"));
        await Node("S1", $"S1_{t}", Text($"Site1{t}"), NaeTreeSchema.LevelSite, "A1");
        var n1 = await Node(
            "N1", $"N1_{t}", new LocalizedText(new Dictionary<string, string> { ["en"] = $"Boiler{t}", ["ru"] = $"Котёл{t}" }),
            NaeTreeSchema.LevelSource, "S1", (NaeTreeSchema.NaeCodeField, $"0036{t}"));
        await Node("T1", $"T1_{t}", Text($"Twin{t}"), NaeTreeSchema.LevelSource, "S1");
        await Node("T2", $"T2_{t}", Text($"Twin{t}"), NaeTreeSchema.LevelSource, "S1");
        await Node("R2", $"R_MAN{t}", Text($"Mangystau{t}"), NaeTreeSchema.LevelRegion, null);
        await Node("S2", $"S2_{t}", Text($"Site2{t}"), NaeTreeSchema.LevelSite, "R2");
        var n2 = await Node("N2", $"N2_{t}", Text($"Heater{t}"), NaeTreeSchema.LevelSource, "S2");
        await Node("R3", $"R3_{t}", Text($"Dup region {t}"), NaeTreeSchema.LevelRegion, null, (NaeTreeSchema.RegionField, $"DUP{t}"));
        var n3 = await Node("N3", $"N3_{t}", Text($"Pump{t}"), NaeTreeSchema.LevelSource, "R3");
        var n4 = await Node("N4", $"N4_{t}", Text($"Loose{t}"), NaeTreeSchema.LevelSource, null);

        RegistryEntry Plain(RegistryDef def, string code, string name)
        {
            var entry = new RegistryEntry(def.Id, EcrCode.Create(code), Text(name), 0, DateTime.UtcNow);
            db.RegistryEntries.Add(entry);
            return entry;
        }

        var aty = Plain(regions, $"RATY{t}", $"ATY{t}");
        var man = Plain(regions, $"RMAN{t}", $"Mangystau{t}");
        Plain(regions, $"RDUP1{t}", $"DUP{t}");
        Plain(regions, $"RDUP2{t}", $"dup{t.ToLowerInvariant()}");
        var off = Plain(areas, $"AOFF{t}", $"Off{t} complex");
        Plain(areas, $"AON{t}", $"Onshore {t}");
        await db.SaveChangesAsync();

        return new World(tree.Code, regions.Code, areas.Code, n1.Id, n2.Id, n3.Id, n4.Id, aty.Id, man.Id, off.Id);
    }
}
