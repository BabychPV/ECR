// tests/Ecr.Api.Tests/MethodologyEffectiveDateRaceTests.cs
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
/// Аудит L7-08 (ФВ-13.3): СПРАВЖНЯ гонка двох публікацій двох чернеток однієї методології на одну
/// дату на живому SQL — рівно одна публікація зберігається, друга відбивається базою і доїжджає
/// клієнтові як 409 <c>ECR-CALC-0409</c>, а не 500 і не «обидві успішні».
/// </summary>
/// <remarks>
/// ⚠ Гонка відтворена на рівні агрегата й бази: кожна публікація — власний <c>DbContext</c>, обидві
/// ПРОЧИТАЛИ методологію й пройшли перевірку «дата зайнята» в пам'яті
/// (<c>Methodology.PublishVersion</c>) ДО того, як будь-яка зберігається (<c>Barrier</c>) — це
/// рівно сценарій аудиту. Крізь <c>PublishMethodologyHandler</c> (золотий набір, чотири ока,
/// діф) гонка не проганялась: для зеленого золотого набору потрібен повний стенд документа, а
/// предмет перевірки — що робить БАЗА, коли пам'ять не встигла. Виняток другої публікації
/// проганяється крізь справжній конвеєр помилок (як у <see cref="MethodologyEffectiveDateClashErrorTests"/>).
/// <para>
/// Мутації: прибрати індекс (на свіжій базі без міграції <c>AN37MethodologyEffectiveUnique</c>)
/// — обидві зберігаються, тест червоний; прибрати арм <c>UQ_MV_Effective</c> у
/// <c>ExceptionHandlingMiddleware</c> — друга відповідь 500, тест червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyEffectiveDateRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 11, 1);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.3")]
    public async Task Дві_паралельні_публікації_на_одну_дату_дають_одну_збережену_і_одну_409_ECR_CALC_0409()
    {
        var (methodologyId, versionIds, publisherId) = await ArrangeAsync();
        using var barrier = new Barrier(2);

        async Task<Exception?> PublishAsync(int versionId)
        {
            try
            {
                await using var db = Context();
                var methodology = await db.Methodologies
                    .Include(m => m.Versions)
                    .SingleAsync(m => m.Id == methodologyId);
                var version = methodology.Versions.Single(v => v.Id == versionId);

                // Обидві вже прочитали стан і ще нічого не зберегли.
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));

                methodology.PublishVersion(version, publisherId, "L7-08 race", From, testsPassed: true, Now);
                await db.SaveChangesAsync();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var results = await Task.WhenAll(
            Task.Run(() => PublishAsync(versionIds[0])),
            Task.Run(() => PublishAsync(versionIds[1])));

        var failures = results.Where(r => r is not null).ToList();

        // Не «обидві успішні» і не «обидві впали».
        Assert.True(failures.Count == 1, $"очікувалась рівно одна відмова, а їх {failures.Count}: {string.Join(" | ", failures)}");

        await using (var check = Context())
        {
            var published = await check.MethodologyVersions
                .Where(v => v.MethodologyId == methodologyId && v.Status == TemplateVersionStatus.Published)
                .CountAsync();
            Assert.Equal(1, published);
        }

        // Відмова — саме порушення UQ_MV_Effective, і клієнтові вона їде як 409 ECR-CALC-0409, не 500.
        var failure = failures[0]!;
        Assert.IsType<DbUpdateException>(failure);
        Assert.Contains("UQ_MV_Effective", failure.GetBaseException().Message, StringComparison.Ordinal);

        var body = await ProblemReservedMembersTests.ProblemTextAsync(failure);
        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-CALC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("2026-11-01", problem.GetProperty("effectiveFrom").GetString());
    }

    /// <summary>Методологія з двома чернетками й двома користувачами (автор, публікатор).</summary>
    private async Task<(int MethodologyId, int[] VersionIds, int PublisherId)> ArrangeAsync()
    {
        await using var db = Context();
        var tag = Guid.NewGuid().ToString("N")[..12];

        var author = new Ecr.Domain.Entities.Security.User($"l708a_{tag}", $"l708a_{tag}", AuthProvider.Local);
        var publisher = new Ecr.Domain.Entities.Security.User($"l708p_{tag}", $"l708p_{tag}", AuthProvider.Local);
        var hash = new Ecr.Infrastructure.Security.PasswordHasher().Hash("L708-Race-2026!");
        author.SetPassword(hash);
        publisher.SetPassword(hash);
        db.Users.AddRange(author, publisher);
        await db.SaveChangesAsync();

        var methodology = new Methodology(
            EcrCode.Create($"L708{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "L7-08 race" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var first = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, author.Id, Now);
        var second = new MethodologyVersion(methodology.Id, "1.1", CalculationLevel.Configuration, author.Id, Now);
        methodology.AddVersion(first);
        methodology.AddVersion(second);
        await db.SaveChangesAsync();

        return (methodology.Id, [first.Id, second.Id], publisher.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
