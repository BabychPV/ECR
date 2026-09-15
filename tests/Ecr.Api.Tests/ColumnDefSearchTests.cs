// tests/Ecr.Api.Tests/ColumnDefSearchTests.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Директива "пошук колонки за назвою замість голого ColumnDefId":
/// <c>MethodologyRequiredInputsPanel</c> і <c>MethodologyBindingsPanel</c>
/// приймали <c>ColumnDefId</c> голим числом у <c>NumberInput</c> —
/// адміністратор мав пам'ятати внутрішній ідентифікатор напам'ять.
/// <c>GET /api/v1/column-defs/search</c> дає той самий прийом, що вже working
/// для довідників (<c>ColumnEditor.tsx</c>, вибір довідника за назвою):
/// пошук за підрядком коду чи заголовка, наскрізь по всіх версіях шаблонів.
/// </summary>
[Collection("SqlServer")]
public sealed class ColumnDefSearchTests(SqlServerFixture sql)
{
    private const string LoginPassword = "Column-Search-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пошук_за_підрядком_заголовка_знаходить_колонку_і_несе_контекст_таблиці()
    {
        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();

        var (userName, tag) = await ArrangeAsync().ConfigureAwait(true);

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = LoginPassword }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        // ⚠ Пошук за словом ІЗ ЗАГОЛОВКА, не з коду — саме той сценарій,
        // заради якого директива існує: адміністратор пам'ятає назву колонки,
        // а не її внутрішній Id чи технічний код.
        var response = await client.GetAsync(
            new Uri($"/api/v1/column-defs/search?q=Volume{tag}", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<System.Text.Json.JsonElement>().ConfigureAwait(true);
        var results = body.EnumerateArray().ToList();

        var match = Assert.Single(results);
        Assert.Equal($"VOL{tag}", match.GetProperty("code").GetString());
        Assert.Equal($"TBL{tag}", match.GetProperty("tableCode").GetString());
        Assert.Equal($"SHEET{tag}", match.GetProperty("sheetCode").GetString());

        // ⛔ Колонка з ІНШИМ заголовком (у тій самій таблиці) не мала
        // потрапити в результат — інакше пошук був би просто «переліком
        // усіх колонок», а не фільтром.
        Assert.DoesNotContain(results, r => r.GetProperty("code").GetString() == $"CAT{tag}");
    }

    /// <summary>Користувач із правом <c>Template.View</c> і дві колонки з різними заголовками.</summary>
    private async Task<(string UserName, string Tag)> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var userName = $"colsearch_{tag}";

        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>()
                .UseSqlServer(sql.ConnectionString)
                .Options);

        var user = new Ecr.Domain.Entities.Security.User(
            userName, userName, Ecr.Domain.Enums.AuthProvider.Local);
        user.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash(LoginPassword));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var role = new Ecr.Domain.Entities.Security.Role(
            Ecr.Domain.ValueObjects.EcrCode.Create($"R{tag}"),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Column search test" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new Ecr.Domain.Entities.Security.RolePermission(role.Id, "Template.View"));
        db.RoleAssignments.Add(new Ecr.Domain.Entities.Security.RoleAssignment(role.Id, user.Id, principalSid: null));

        var template = new Ecr.Domain.Entities.Configuration.Template(
            Ecr.Domain.ValueObjects.EcrCode.Create($"T{tag}"),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = "Column search template" }),
            user.Id, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new Ecr.Domain.Entities.Configuration.TemplateVersion(
            template.Id, "1.0.0.0", user.Id, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new Ecr.Domain.Entities.Configuration.SheetDef(
            version.Id, Ecr.Domain.ValueObjects.EcrCode.Create($"SHEET{tag}"),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table = new Ecr.Domain.Entities.Configuration.TableDef(
            sheet.Id, Ecr.Domain.ValueObjects.EcrCode.Create($"TBL{tag}"),
            new Ecr.Domain.ValueObjects.LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            Ecr.Domain.Enums.TableLayoutKind.PerPeriodInstance, Ecr.Domain.Enums.TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var volume = new Ecr.Domain.Entities.Configuration.ColumnDef(
            table.Id, Ecr.Domain.ValueObjects.EcrCode.Create($"VOL{tag}"),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = $"Volume{tag} extracted" }),
            1, Ecr.Domain.Enums.CellDataType.Decimal);
        var category = new Ecr.Domain.Entities.Configuration.ColumnDef(
            table.Id, Ecr.Domain.ValueObjects.EcrCode.Create($"CAT{tag}"),
            new Ecr.Domain.ValueObjects.LocalizedText(
                new Dictionary<string, string> { ["en"] = $"Category{tag}" }),
            2, Ecr.Domain.Enums.CellDataType.String);
        db.ColumnDefs.Add(volume);
        db.ColumnDefs.Add(category);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (userName, tag);
    }
}
