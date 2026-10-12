// tests/Ecr.Api.Tests/ColumnBatchApiTests.cs
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
/// G13 (Land): <c>PUT /template-versions/{id}/tables/{tableId}/columns</c> — пакетний запис колонок
/// наскрізно через HTTP на справжній базі (маршрут, серіалізація, транзакція, читання одиночним <c>GET</c>).
/// </summary>
[Collection("SqlServer")]
public sealed class ColumnBatchApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-G13-Batch-2026!";

    private static readonly DateTime Now = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пачка_колонок_створюється_одним_запитом_і_читається_одиночним_GET()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Template.Edit", "Template.View").ConfigureAwait(true);
        var (versionId, tableId) = await SeedDraftTableAsync().ConfigureAwait(true);

        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/columns", UriKind.Relative),
            new
            {
                columns = Enumerable.Range(0, 40).Select(i => new
                {
                    code = $"M{i:00}",
                    headerL10n = new Dictionary<string, string> { ["en"] = $"Month {i}" },
                    ordinal = (int?)null,
                    dataType = "Decimal",
                    isRequired = i % 2 == 0,
                    isReadOnly = false,
                    isHidden = false,
                    precision = (byte?)18,
                    scale = (byte?)4,
                    defaultValue = (string?)null,
                    displayFormat = (string?)null,
                    styleId = (int?)null,
                    lookupRegistryDefId = (int?)null,
                    lookupFilter = (string?)null,
                    unitId = (int?)null,
                }).ToList(),
            }).ConfigureAwait(true);

        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {app.ErrorsText}");

        var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(40, saved.GetArrayLength());
        Assert.Equal("M00", saved[0].GetProperty("code").GetString());
        Assert.Equal("M39", saved[39].GetProperty("code").GetString());
        Assert.Equal(39, saved[39].GetProperty("ordinal").GetInt32());

        var one = await client
            .GetAsync(new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/columns/M07", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.True(one.IsSuccessStatusCode, $"{one.StatusCode}: {app.ErrorsText}");
        var column = JsonDocument.Parse(await one.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal(18, column.GetProperty("precision").GetInt32());
        Assert.False(column.GetProperty("isRequired").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пачка_понад_ліміт_422_з_ключем()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Template.Edit").ConfigureAwait(true);
        var (versionId, tableId) = await SeedDraftTableAsync().ConfigureAwait(true);

        var response = await client.PutAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/tables/{tableId}/columns", UriKind.Relative),
            new
            {
                columns = Enumerable.Range(0, 501).Select(i => new
                {
                    code = $"M{i:000}",
                    headerL10n = new Dictionary<string, string> { ["en"] = "x" },
                    dataType = "String",
                    isRequired = false,
                    isReadOnly = false,
                    isHidden = false,
                }).ToList(),
            }).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Contains("columnBatchTooLarge", text, StringComparison.Ordinal);
    }

    private async Task<(int VersionId, int TableId)> SeedDraftTableAsync()
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var template = new Template(EcrCode.Create($"TG13{tag}"), Name($"G13 {tag}"), createdByUserId: 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new TemplateVersion(template.Id, "1.0.0.0", createdByUserId: 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var sheet = new SheetDef(version.Id, EcrCode.Create($"SH{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"TB{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (version.Id, table.Id);
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
        var name = $"g13_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("G13 batch test"));

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
