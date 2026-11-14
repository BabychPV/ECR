// tests/Ecr.Api.Tests/MonthInputAliasesApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// ✎ LAND-YEAR L0b: <c>GET/PUT /template-versions/{id}/month-input-aliases</c> наскрізно через HTTP на справжній базі —
/// заміна всього набору чернетки, відмова опублікованій версії (409), перевірка правилами публікації (422) і право
/// <c>Template.Edit</c> (Allow/Deny пара на тій самій чернетці).
/// </summary>
[Collection("SqlServer")]
public sealed class MonthInputAliasesApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-L0b-Aliases-2026!";

    private static readonly DateTime Now = new(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-474")]
    public async Task PUT_замінює_весь_набір_чернетки_а_GET_його_читає()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Template.Edit", "Template.View").ConfigureAwait(true);
        var s = await SeedAsync(published: false).ConfigureAwait(true);

        var first = await PutAsync(client, s, [Item(s, "Land_Gas", 9, "M09"), Item(s, "Land_Gas", 10, "M10")]).ConfigureAwait(true);
        Assert.True(first.IsSuccessStatusCode, $"{first.StatusCode}: {await first.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");

        var read = await GetAsync(client, s).ConfigureAwait(true);
        Assert.Equal(2, read.GetProperty("items").GetArrayLength());
        var oct = read.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("monthNumber").GetInt32() == 10);
        Assert.Equal("Land_Gas", oct.GetProperty("alias").GetString());
        Assert.Equal(s.Columns["M10"].Code, oct.GetProperty("columnCode").GetString());
        Assert.Equal(s.Columns["M10"].Id, oct.GetProperty("columnDefId").GetInt32());
        Assert.Equal(s.TableCode, oct.GetProperty("tableCode").GetString());

        // Повторний PUT — ЗАМІНА: лишається рівно новий набір (той самий ключ UQ — без конфлікту порядку DELETE/INSERT).
        var second = await PutAsync(client, s, [Item(s, "Land_Gas", 10, "M10")]).ConfigureAwait(true);
        Assert.True(second.IsSuccessStatusCode, $"{second.StatusCode}: {app.ErrorsText}");
        Assert.Equal([(10, s.Columns["M10"].Id)], await StoredAsync(s).ConfigureAwait(true));

        // Порожній набір — прибрати всі.
        var empty = await PutAsync(client, s, []).ConfigureAwait(true);
        Assert.True(empty.IsSuccessStatusCode, $"{empty.StatusCode}: {app.ErrorsText}");
        Assert.Empty(await StoredAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-474")]
    public async Task Опублікована_версія_409_нічого_не_записано()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Template.Edit", "Template.View").ConfigureAwait(true);
        var s = await SeedAsync(published: true).ConfigureAwait(true);

        var response = await PutAsync(client, s, [Item(s, "Land_Gas", 9, "M09")]).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("ECR-TMPL-0409", await response.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);
        Assert.Empty(await StoredAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-474")]
    public async Task Хибний_набір_422_правилами_публікації_і_наявний_набір_цілий()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Template.Edit", "Template.View").ConfigureAwait(true);
        var s = await SeedAsync(published: false).ConfigureAwait(true);
        var ok = await PutAsync(client, s, [Item(s, "Land_Gas", 9, "M09")]).ConfigureAwait(true);
        Assert.True(ok.IsSuccessStatusCode, $"{ok.StatusCode}: {app.ErrorsText}");

        // Ціль іншого місяця (M09 для жовтня) — правило публікації L0a.
        var wrongMonth = await PutAsync(client, s, [Item(s, "Land_Gas", 10, "M09")]).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongMonth.StatusCode);
        Assert.Contains("monthInputAliasInvalid", await wrongMonth.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);

        // Ім'я = код живої колонки.
        var codeClash = await PutAsync(client, s, [Item(s, s.Columns["M09"].Code, 9, "M09")]).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, codeClash.StatusCode);

        // Невідома таблиця й дублікат.
        var unknown = await PutAsync(
            client, s,
            [
                new { sheetCode = s.SheetCode, tableCode = "NO_SUCH", alias = "Land_Gas", monthNumber = 9, columnCode = "M09" },
            ]).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        var duplicate = await PutAsync(client, s, [Item(s, "Land_Gas", 9, "M09"), Item(s, "LAND_GAS", 9, "M09")]).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);

        // Нічого з відхиленого не записано: лишився попередній набір.
        Assert.Equal([(9, s.Columns["M09"].Id)], await StoredAsync(s).ConfigureAwait(true));
    }

    /// <summary>Allow/Deny пара на тій самій чернетці: лише <c>Template.View</c> — 403 і нічого не записано; з <c>Template.Edit</c> — 200.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-474")]
    public async Task Без_Template_Edit_403_з_ним_200()
    {
        using var app = new EcrApiFactory(sql);
        var s = await SeedAsync(published: false).ConfigureAwait(true);

        using (var viewer = await SignedInAsync(app, "Template.View").ConfigureAwait(true))
        {
            var denied = await PutAsync(viewer, s, [Item(s, "Land_Gas", 9, "M09")]).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Empty(await StoredAsync(s).ConfigureAwait(true));

            // Читання — право перегляду.
            var read = await GetAsync(viewer, s).ConfigureAwait(true);
            Assert.Equal(0, read.GetProperty("items").GetArrayLength());
        }

        using var editor = await SignedInAsync(app, "Template.Edit", "Template.View").ConfigureAwait(true);
        var allowed = await PutAsync(editor, s, [Item(s, "Land_Gas", 9, "M09")]).ConfigureAwait(true);
        Assert.True(allowed.IsSuccessStatusCode, $"{allowed.StatusCode}: {app.ErrorsText}");
        Assert.Equal([(9, s.Columns["M09"].Id)], await StoredAsync(s).ConfigureAwait(true));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record Scenario(int VersionId, int TableId, string SheetCode, string TableCode, Dictionary<string, ColumnDef> Columns);

    private static object Item(Scenario s, string alias, int month, string column)
        => new { sheetCode = s.SheetCode, tableCode = s.TableCode, alias, monthNumber = month, columnCode = s.Columns[column].Code };

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Scenario s, IReadOnlyList<object> items)
        => await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{s.VersionId}/month-input-aliases", UriKind.Relative),
            new { items }).ConfigureAwait(false);

    private static async Task<JsonElement> GetAsync(HttpClient client, Scenario s)
    {
        using var response = await client
            .GetAsync(new Uri($"/api/v1/template-versions/{s.VersionId}/month-input-aliases", UriKind.Relative))
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<List<(int Month, int ColumnId)>> StoredAsync(Scenario s)
    {
        await using var db = new EcrDbContext(Options());
        return [.. (await db.MonthInputAliases.AsNoTracking()
                .Where(a => a.TableDefId == s.TableId)
                .Select(a => new { a.MonthNumber, a.ColumnDefId })
                .ToListAsync().ConfigureAwait(false))
            .Select(a => ((int)a.MonthNumber, a.ColumnDefId))
            .OrderBy(a => a.Item1)];
    }

    /// <summary>Чернетка (чи опублікована) версія «рік, місяці колонками»: таблиця «місяці в колонках», колонки M09/M10.</summary>
    private async Task<Scenario> SeedAsync(bool published)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var template = new Template(EcrCode.Create($"TL0B{tag}"), Name($"L0b {tag}"), createdByUserId: 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, Now);
        version.SetLayout(TemplateLayout.YearWithMonthColumns);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new SheetDef(version.Id, EcrCode.Create($"SH{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"TB{tag}"), Name("Table"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var columns = new Dictionary<string, ColumnDef>();
        foreach (var m in new[] { 9, 10 })
        {
            var column = new ColumnDef(table.Id, EcrCode.Create($"C_M{m:00}"), Name($"M{m}"), m, CellDataType.Decimal);
            column.SetMonth(true, (byte)m);
            db.ColumnDefs.Add(column);
            columns[$"M{m:00}"] = column;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        if (published)
        {
            db.Entry(version).Property(v => v.Status).CurrentValue = TemplateVersionStatus.Published;
            db.Entry(version).Property(v => v.PublishedAt).CurrentValue = Now;
            db.Entry(version).Property(v => v.PublishedByUserId).CurrentValue = 1;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new Scenario(version.Id, table.Id, sheet.Code, table.Code, columns);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    /// <summary>Клієнт із чинним сеансом і заданими правами.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"l0b_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("L0b aliases test"));
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
}
