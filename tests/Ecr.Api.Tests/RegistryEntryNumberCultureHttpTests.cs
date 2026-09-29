// tests/Ecr.Api.Tests/RegistryEntryNumberCultureHttpTests.cs
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
/// Число текстом у записі довідника — за мовою користувача, не «12,5 → 125»: ручний upsert
/// (<c>POST …/entries</c>) і пакет RT-14 (<c>POST …/entries/batch</c>) наскрізь через HTTP і SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Червоне до фіксу: <c>RegistryValue.Set</c> → <c>Convert.ToDecimal(…, Invariant)</c> бере кому
/// розрядами — upsert «12,5» зберігав 125, «1,234» в en-US — 1234 без відмови, а пакет із «12,5» і
/// <c>12.5</c> для ключового поля записував два записи (125 і 12.5) замість дубля ключа.
/// <para>
/// Мутаційний доказ: у <c>UpsertRegistryEntryHandler</c> передати writer'у <c>dto.Values</c> замість
/// розібраних → червоні обидва тести upsert; у <c>RegistryBatchHandler</c> — <c>Item.Values</c> замість
/// <c>state.Values</c> → червоні обидва тести пакета.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryEntryNumberCultureHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Culture-2026!";

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("ru-RU", "12,5", "12.5")]
    [InlineData("en-US", "12,5", "12.5")]
    [InlineData("ru-RU", "1 234,5", "1234.5")]
    [InlineData("en-US", "1,234.5", "1234.5")]
    public async Task Upsert_читає_число_за_мовою_користувача(string language, string text, string expected)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, language);
        var f = await SeedAsync();

        using var response = await UpsertAsync(client, f, $"N{f.Tag}", text);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        Assert.Equal(
            decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            await StoredAsync(JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64(), f.QtyFieldId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Upsert_неоднозначне_в_en_відхилено_і_нічого_не_створено()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "en-US");
        var f = await SeedAsync();

        using var response = await UpsertAsync(client, f, $"N{f.Tag}", "1,234");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}");
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("err.ECR-REG-0422.valueNotNumber", problem.GetProperty("messageKey").GetString());
        Assert.Equal("ambiguousSeparator", problem.GetProperty("reason").GetString());
        Assert.Equal("1234", problem.GetProperty("asGroup").GetString());
        Assert.Equal("1.234", problem.GetProperty("asDecimal").GetString());

        await using var db = new EcrDbContext(Options());
        Assert.False(await db.RegistryEntries.AnyAsync(e => e.RegistryDefId == f.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Пакет_бачить_розібране_число_в_ключі_дубль_а_не_два_записи()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "ru-RU");
        var f = await SeedAsync();

        var (status, body) = await BatchAsync(client, f,
            new { clientRowId = "text", op = "upsert", code = $"A{f.Tag}", values = new Dictionary<string, object?> { ["QTY"] = "12,5" } },
            new { clientRowId = "json", op = "upsert", code = $"B{f.Tag}", values = new Dictionary<string, object?> { ["QTY"] = 12.5m } });

        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        Assert.False(body.GetProperty("applied").GetBoolean(), body.ToString());
        foreach (var row in body.GetProperty("rows").EnumerateArray())
        {
            Assert.Equal("error", row.GetProperty("status").GetString());
            Assert.Equal("err.ECR-REG-4092.keyDuplicateInBatch", row.GetProperty("errors")[0].GetProperty("messageKey").GetString());
        }

        await using var db = new EcrDbContext(Options());
        Assert.False(await db.RegistryEntries.AnyAsync(e => e.RegistryDefId == f.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакет_неоднозначне_число_помилка_рядка()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "en-US");
        var f = await SeedAsync();

        var (status, body) = await BatchAsync(client, f,
            new { clientRowId = "ok", op = "upsert", code = $"A{f.Tag}", values = new Dictionary<string, object?> { ["QTY"] = "12,5" } },
            new { clientRowId = "amb", op = "upsert", code = $"B{f.Tag}", values = new Dictionary<string, object?> { ["QTY"] = "1,234" } });

        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        Assert.False(body.GetProperty("applied").GetBoolean(), body.ToString());
        var rows = body.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("added", rows[0].GetProperty("status").GetString());
        Assert.Equal("error", rows[1].GetProperty("status").GetString());
        var error = rows[1].GetProperty("errors")[0];
        Assert.Equal("ECR-REG-0422", error.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-0422.valueNotNumber", error.GetProperty("messageKey").GetString());
        Assert.Equal("QTY", error.GetProperty("field").GetString());
        Assert.Equal("ambiguousSeparator", error.GetProperty("params").GetProperty("reason").GetString());

        await using var db = new EcrDbContext(Options());
        Assert.False(await db.RegistryEntries.AnyAsync(e => e.RegistryDefId == f.Id));
    }

    private static Task<HttpResponseMessage> UpsertAsync(HttpClient client, Fixture f, string code, object? qty)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{f.Code}/entries", UriKind.Relative),
            new
            {
                id = (long?)null,
                registryDefId = f.Id,
                code,
                display = new { values = new Dictionary<string, string> { ["en"] = code } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?> { ["QTY"] = qty },
            });

    private static async Task<(HttpStatusCode Status, JsonElement Body)> BatchAsync(HttpClient client, Fixture f, params object[] items)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{f.Code}/entries/batch?dryRun=false", UriKind.Relative), new { items });
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<decimal?> StoredAsync(long entryId, int fieldId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryEntryId == entryId && v.RegistryFieldDefId == fieldId)
            .Select(v => v.ValueNumeric)
            .SingleAsync();
    }

    /// <summary>Довідник: QTY (Decimal) — первинний ключ.</summary>
    private async Task<Fixture> SeedAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var registry = new RegistryDef(EcrCode.Create($"RN{tag}"), Name($"Numbers {tag}"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var qty = new RegistryFieldDef(registry.Id, EcrCode.Create("QTY"), Name("Qty"), CellDataType.Decimal, 1);
        qty.Update(Name("Qty"), 1, isRequired: true);
        db.RegistryFieldDefs.Add(qty);
        await db.SaveChangesAsync();

        db.RegistryKeyDefs.Add(new RegistryKeyDef(
            registry.Id, EcrCode.Create("PK"), Name("Primary"), [qty],
            isPrimary: true, ignoreCase: true, createdByUserId: 0, DateTime.UtcNow));
        await db.SaveChangesAsync();

        return new Fixture(registry.Id, registry.Code, tag, qty.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string acceptLanguage)
    {
        var name = $"regn_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry culture test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.View"));
            db.RolePermissions.Add(new RolePermission(role.Id, "Registry.EditData"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(acceptLanguage);
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private sealed record Fixture(int Id, string Code, string Tag, int QtyFieldId);
}
