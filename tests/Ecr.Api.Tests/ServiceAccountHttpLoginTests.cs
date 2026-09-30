// tests/Ecr.Api.Tests/ServiceAccountHttpLoginTests.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// (є) Службовим записом <c>svc-integration</c> не входять через HTTP — навіть
/// з ПРАВИЛЬНИМ паролем.
/// </summary>
/// <remarks>
/// ⛔ Умова 3 до права запису інтеграції. До зміни вхід цим записом тримався
/// лише на тому, що випадкового пароля із сіду ніхто не знає; скидання пароля
/// адміністратором (<c>POST /security/users/{id}/password</c>) цю умову знімало.
/// Тест відтворює саме той стан: пароль відомий — і вхід однаково відхилено.
///
/// ⚠ Запис спільний для всієї тестової бази, тому хеш пароля повертається у
/// <c>finally</c>. Невдала спроба лічильника спроб не крутить (гілка
/// «невідомий запис»), тож блокування запису тест по собі не лишає.
/// </remarks>
[Collection("SqlServer")]
public sealed class ServiceAccountHttpLoginTests(SqlServerFixture sql)
{
    private const string Password = "Svc-Integration-Known-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-writer")]
    public async Task Вхід_svc_integration_із_відомим_паролем_відхиляється_як_невідомий_запис()
    {
        string? original;

        await using (var db = CreateContext())
        {
            var svc = await db.Users.SingleAsync(u => u.UserName == User.IntegrationServiceUserName);
            original = svc.PasswordHash;
            svc.SetPassword(new PasswordHasher().Hash(Password));
            await db.SaveChangesAsync();
        }

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = app.CreateClient();

            var service = await client.PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = User.IntegrationServiceUserName, password = Password });

            var unknown = await client.PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative),
                new { userName = "zzz-no-such-user-5b1e", password = Password });

            Assert.Equal(HttpStatusCode.Unauthorized, service.StatusCode);
            Assert.False(service.Headers.Contains("Set-Cookie"), "Службовому запису видано cookie.");

            // ⚠ Та сама відповідь, що на невідоме ім'я: форма входу не підтверджує
            // існування службового запису.
            Assert.Equal(unknown.StatusCode, service.StatusCode);
            var (serviceCode, serviceDetail) = await ProblemAsync(service);
            var (unknownCode, unknownDetail) = await ProblemAsync(unknown);
            Assert.Equal("ECR-AUTH-0401", serviceCode);
            Assert.Equal(unknownCode, serviceCode);
            Assert.Equal(unknownDetail, serviceDetail);
        }
        finally
        {
            await using var db = CreateContext();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE sec.[User] SET PasswordHash = {original} WHERE UserName = {User.IntegrationServiceUserName}");
        }
    }

    private static async Task<(string? ErrorCode, string? Detail)> ProblemAsync(HttpResponseMessage response)
    {
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        return (
            root.TryGetProperty("errorCode", out var code) ? code.GetString() : null,
            root.TryGetProperty("detail", out var detail) ? detail.GetString() : null);
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);
}
