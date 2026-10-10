// tests/Ecr.Api.Tests/MethodologyCategoryRuleRaceTests.cs
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
/// N2-05 (AN-72): два одночасні ПЕРШІ збереження правила категорії однієї версії — порушення
/// <c>UQ_CategoryRule_Version</c> віддається клієнтові як 409 <c>ECR-CALC-0409</c>, а не голим 500.
/// </summary>
/// <remarks>
/// ⚠ Гонка відтворена на рівні агрегата й бази (як <see cref="MethodologyEffectiveDateRaceTests"/>): обидві
/// сторони ПРОЧИТАЛИ «правила немає» ДО того, як будь-яка зберігається (<c>Barrier</c>). Через HTTP два
/// <c>PUT</c> недетерміновані: другий міг би прийти після першого й чесно переписати правило (200 + 200).
/// <para>
/// Мутація: прибрати арм <c>UQ_CategoryRule_Version</c> з <c>ExceptionHandlingMiddleware</c> — відповідь 500,
/// червоні обидва тести.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyCategoryRuleRaceTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Порушення_UQ_CategoryRule_Version_віддається_як_409_ECR_CALC_0409_з_ключем_каталогу()
    {
        var body = await ProblemReservedMembersTests.ProblemTextAsync(new DbUpdateException(
            "save failed",
            new InvalidOperationException(
                "Cannot insert duplicate key row in object 'calc.CategoryRule' with unique index "
                + "'UQ_CategoryRule_Version'. The duplicate key value is (81).")));

        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-CALC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CALC-0409.categoryRuleConcurrent", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_паралельні_перші_збереження_правила_дають_одне_збережене_і_одне_409()
    {
        var versionId = await ArrangeAsync();
        using var barrier = new Barrier(2);

        async Task<Exception?> SaveAsync(string expression)
        {
            try
            {
                await using var db = Context();
                var version = await db.MethodologyVersions.SingleAsync(v => v.Id == versionId);
                var existing = await db.MethodologyCategoryRules
                    .SingleOrDefaultAsync(r => r.MethodologyVersionId == versionId);
                Assert.Null(existing);

                // Обидві вже прочитали «правила немає» і ще нічого не зберегли.
                barrier.SignalAndWait(TimeSpan.FromSeconds(30));

                db.MethodologyCategoryRules.Add(version.SetCategoryRule(existing, expression, Now));
                await db.SaveChangesAsync();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync("'Diesel'")),
            Task.Run(() => SaveAsync("'Gas'")));

        var failures = results.Where(r => r is not null).ToList();
        Assert.True(
            failures.Count == 1,
            $"очікувалась рівно одна відмова, а їх {failures.Count}: {string.Join(" | ", failures)}");

        await using (var check = Context())
        {
            Assert.Equal(1, await check.MethodologyCategoryRules.CountAsync(r => r.MethodologyVersionId == versionId));
        }

        var failure = failures[0]!;
        Assert.IsType<DbUpdateException>(failure);
        Assert.Contains("UQ_CategoryRule_Version", failure.GetBaseException().Message, StringComparison.Ordinal);

        var problem = JsonDocument.Parse(await ProblemReservedMembersTests.ProblemTextAsync(failure)).RootElement;
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-CALC-0409", problem.GetProperty("errorCode").GetString());
    }

    /// <summary>Методологія з однією чернеткою без правила категорії; повертає Id версії.</summary>
    private async Task<int> ArrangeAsync()
    {
        await using var db = Context();
        var tag = Guid.NewGuid().ToString("N")[..12];

        var author = new Ecr.Domain.Entities.Security.User($"n205a_{tag}", $"n205a_{tag}", AuthProvider.Local);
        author.SetPassword(new Ecr.Infrastructure.Security.PasswordHasher().Hash("N205-Race-2026!"));
        db.Users.Add(author);
        await db.SaveChangesAsync();

        var methodology = new Methodology(
            EcrCode.Create($"N205{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "N2-05 race" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, author.Id, Now);
        methodology.AddVersion(version);
        await db.SaveChangesAsync();

        return version.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
