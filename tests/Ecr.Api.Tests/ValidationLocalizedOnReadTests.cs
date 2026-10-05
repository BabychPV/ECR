// tests/Ecr.Api.Tests/ValidationLocalizedOnReadTests.cs
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
/// T2-07 / T3-03 / T4-06: збережений результат перевірки віддається мовою ЧИТАЧА, а не того, хто валідував.
/// </summary>
/// <remarks>
/// ⛔ До фіксу <c>wf.ValidationResult.MessagesJson</c> зберігав готовий текст мовою автора запуску, і
/// <c>GET …/validation</c> віддавав його всім читачам як є. Тепер повідомлення двигуна несуть
/// <c>MessageKey</c> + <c>Params</c>, а читання збирає текст із каталогу <c>sys_ecr.UiString</c> (сід
/// <c>COLL:an42vm</c>) мовою читача. Тест живий: справжній сід, справжній каталог, справжнє збереження.
/// </remarks>
[Collection("SqlServer")]
public sealed class ValidationLocalizedOnReadTests(SqlServerFixture sql)
{
    private const string Password = "Api-Val-I18n-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "T2-07")]
    public async Task Збережений_результат_валідації_en_читається_мовою_читача_ru_і_kz()
    {
        var (documentId, period, userName) = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");

        // Валідує англомовний користувач — у базі лежить результат автора запуску.
        using var validate = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/documents/{documentId}/validate", UriKind.Relative))
        {
            Content = JsonContent.Create(new { periodKey = period }),
        };
        validate.Headers.AcceptLanguage.ParseAdd("en");
        var fresh = await client.SendAsync(validate).ConfigureAwait(true);
        var freshBody = await fresh.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(fresh.IsSuccessStatusCode, $"POST: {fresh.StatusCode}\n{freshBody}\n{app.ErrorsText}");
        Assert.Contains("is required", freshBody, StringComparison.Ordinal);

        // Читачі з іншими мовами бачать ЗБЕРЕЖЕНИЙ результат своєю мовою.
        Assert.Contains("is required", await ReadAsync(client, documentId, period, "en").ConfigureAwait(true), StringComparison.Ordinal);
        Assert.Contains("обязательна", await ReadAsync(client, documentId, period, "ru").ConfigureAwait(true), StringComparison.Ordinal);
        Assert.Contains("міндетті", await ReadAsync(client, documentId, period, "kz").ConfigureAwait(true), StringComparison.Ordinal);

        // Назовні лише текст: ні ключа, ні підстановок у контракт не потрапляє.
        var ru = await ReadAsync(client, documentId, period, "ru").ConfigureAwait(true);
        Assert.DoesNotContain("messageKey", ru, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("params", ru, StringComparison.OrdinalIgnoreCase);

        // Сам збережений JSON несе ключ + підстановки (а не лише текст).
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var json = await db.ValidationResults
            .Where(r => r.DocumentId == documentId).Select(r => r.MessagesJson).FirstAsync().ConfigureAwait(true);
        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement.EnumerateArray().First();
        Assert.Equal("validation.column.required", first.GetProperty("MessageKey").GetString());
        Assert.Equal(JsonValueKind.Object, first.GetProperty("Params").ValueKind);
    }

    private static async Task<string> ReadAsync(HttpClient client, long documentId, int period, string language)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri($"/api/v1/documents/{documentId}/validation?periodKey={period}", UriKind.Relative));
        request.Headers.AcceptLanguage.ParseAdd(language);
        var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"GET ({language}): {response.StatusCode}\n{body}");
        return body;
    }

    private async Task<(long DocumentId, int Period, string UserName)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        // Обов'язкова колонка без значення — повідомлення двигуна із ключем каталогу.
        var column = await db.ColumnDefs.FirstAsync(c => c.TableDefId == document.TableDefId).ConfigureAwait(false);
        column.SetRequired(true);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var userName = $"vloc_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(EcrCode.Create($"VLOC_{Guid.NewGuid():N}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Validation reader" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Read));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (document.DocumentId, document.PeriodKey.Value, userName);
    }
}
