// tests/Ecr.Api.Tests/ApproverReportDefinitionTests.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Q-153, рішення людини 2026-09-28 (<c>D-203</c>): вбудований погоджувач
/// заводить описи державних звітів справжнім HTTP, а ролі без права — ні.
/// </summary>
/// <remarks>
/// ⛔ Користувач отримує ВБУДОВАНУ роль із seed, а не власну з переліком прав:
/// перевіряється саме склад ролі, який дає розгортання. Власна роль із
/// <c>Report.EditDefinition</c> довела б лише, що обробник перевіряє право, —
/// це вже тримає <c>ReportSnapshotRowsTests</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class ApproverReportDefinitionTests(SqlServerFixture sql)
{
    private const string Password = "Api-Approver-Rpt-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Погоджувач_заводить_опис_звіту()
    {
        using var app = new EcrApiFactory(sql);
        using var approver = await SignedInWithBuiltInRoleAsync(app, "Approver").ConfigureAwait(true);

        var response = await CreateDefinitionAsync(approver).ConfigureAwait(true);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(true)} {app.ErrorsText}");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    [InlineData("DataEntry")]
    [InlineData("Viewer")]
    [InlineData("SystemAdministrator")]
    public async Task Вбудована_роль_без_явного_права_опису_не_заводить(string role)
    {
        // ⛔ SystemAdministrator тут навмисно: шаблон `%` бере все безпечне, і
        // якби з права зняли позначку `IsDangerous`, авторство державної форми
        // приїхало б йому мовчки — рівно той дефект, від якого вона стоїть.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInWithBuiltInRoleAsync(app, role).ConfigureAwait(true);

        var response = await CreateDefinitionAsync(client).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static Task<HttpResponseMessage> CreateDefinitionAsync(HttpClient client)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/reports", UriKind.Relative),
            new
            {
                code = $"APR{Guid.NewGuid():N}"[..11],
                nameL10n = new Dictionary<string, string> { ["en"] = "Q-153 approver report" },
                isRegulatory = true,
                version = "1.0",
                columns = new[] { new { code = "Value", kind = "number" } },
                rules = (object?)null,
            });

    /// <summary>Новий локальний користувач із ВБУДОВАНОЮ роллю з seed; входить локально.</summary>
    private async Task<HttpClient> SignedInWithBuiltInRoleAsync(EcrApiFactory app, string roleCode)
    {
        var name = $"q153_{Guid.NewGuid():N}"[..20];
        var roleId = await RoleIdAsync(roleCode).ConfigureAwait(false);

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RoleAssignments.Add(new RoleAssignment(roleId, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private async Task<int> RoleIdAsync(string roleCode)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM sec.Role WHERE Code = @code AND IsBuiltIn = 1";
        command.Parameters.AddWithValue("@code", roleCode);
        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
