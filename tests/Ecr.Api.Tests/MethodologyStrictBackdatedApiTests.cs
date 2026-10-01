using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ФВ-9.9: перехід методології з <c>Legacy</c> у <c>Strict</c> заднім числом
/// відхиляється — по HTTP повний конвеєр дає <c>422</c> з ключем
/// <c>err.ECR-CALC-0422.strictBackdated</c> і підставленими датами.
/// </summary>
/// <remarks>
/// Мутація: прибрати виклик <c>RejectBackdatedStrict</c> з
/// <c>PublishMethodologyHandler</c> — відповідь лишається 422, але вже про порожній
/// золотий набір (інший <c>messageKey</c>), тож перевірка ключа червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyStrictBackdatedApiTests(SqlServerFixture sql)
{
    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.9")]
    public async Task Публікація_Strict_із_сьогоднішньою_або_минулою_датою_дає_422_зі_strictBackdated(int offsetDays)
    {
        var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Calculation.View", "Calculation.Publish");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(offsetDays).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.StrictVersionId}/publish", UriKind.Relative),
            new { changeReason = "ФВ-9.9", effectiveFrom = from });

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {body}");

        using var problem = JsonDocument.Parse(body);
        Assert.Equal("ECR-CALC-0422", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CALC-0422.strictBackdated", problem.RootElement.GetProperty("messageKey").GetString());
    }

    private async Task<Stand> ArrangeAsync()
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var methodology = new Methodology(
            EcrCode.Create($"SBD{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Strict backdated" }));
        var created = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        var legacy = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, created);
        var strict = new MethodologyVersion(methodology.Id, "2.0", CalculationLevel.Configuration, createdByUserId: 1, created);
        strict.SetModes(NumericMode.Strict, strict.CalendarMode, strict.TraceLevel);
        methodology.AddVersion(legacy);
        methodology.AddVersion(strict);

        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        // Попередня версія — опублікована Legacy, чинна задовго до «сьогодні»;
        // публікує інший користувач (чотири ока), тож він має існувати в базі.
        var name = $"sbd_{Guid.NewGuid():N}"[..20];
        var publisher = new Ecr.Domain.Entities.Security.User(name, name, AuthProvider.Local);
        publisher.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash("Api-Strict-Backdated-2026!"));
        db.Users.Add(publisher);
        await db.SaveChangesAsync();

        methodology.PublishVersion(
            legacy, publisher.Id, "ФВ-9.9 arrange", new DateOnly(2020, 1, 1), testsPassed: true, created);
        await db.SaveChangesAsync();

        return new Stand(methodology.Id, strict.Id);
    }

    private sealed record Stand(int MethodologyId, int StrictVersionId);
}
