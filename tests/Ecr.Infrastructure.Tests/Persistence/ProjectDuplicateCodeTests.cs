// tests/Ecr.Infrastructure.Tests/Persistence/ProjectDuplicateCodeTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Finding 2: створення проєкту з уже зайнятим кодом падало НЕОБРОБЛЕНИМ
/// <c>500 ECR-SYS-0500</c> — тост «Внутрішня помилка», модалка лишалася
/// відкритою без жодної підказки, що саме код зайнятий.
/// </summary>
/// <remarks>
/// ⛔ <c>CreateProjectHandler</c> не мав перевірки коду заздалегідь узагалі
/// (на відміну від, наприклад, <c>CreateRegistryHandler</c>, у якого є хоча б
/// пре-чек): другий запит тим самим кодом доходив до
/// <c>uow.SaveChangesAsync</c>, і <c>UQ_Project_Code</c> ловив дублікат уже
/// в БАЗІ — винятком, якого до фіксу не ловив ніхто.
/// </remarks>
[Collection("SqlServer")]
public sealed class ProjectDuplicateCodeTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "lane1-2-4-unhandled-500-pattern")]
    public async Task Дублікат_коду_проєкту_дає_ECR_PRJ_0409_а_не_сирий_виняток_бази()
    {
        var code = $"DUPPRJ_{_tag}";

        await using var db = Context();

        var template = new Template(EcrCode.Create($"TPL_{_tag}"), Text("Template"), 1, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        // periodPolicyId 1 — сіяна політика "ECR-Standard" (той самий факт,
        // на який спираються ErrorContractTests.ArrangeAsync і
        // TestDocumentBuilder).
        var first = new Project(
            EcrCode.Create(code), Text("First"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        db.Projects.Add(first);
        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);

        // Другий запис — ОКРЕМИЙ DbContext (як окремий HTTP-запит), той самий
        // код: саме цей шлях доходив би необробленим до
        // ExceptionHandlingMiddleware до фіксу.
        await using var db2 = Context();
        var duplicate = new Project(
            EcrCode.Create(code), Text("Second"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        db2.Projects.Add(duplicate);

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new UnitOfWork(db2).SaveChangesAsync(CancellationToken.None));

        Assert.Equal("ECR-PRJ-0409", thrown.ErrorCode);
        Assert.Contains(code, thrown.Message, StringComparison.Ordinal);

        // ⛔ Q-30x: без Details["messageKey"] подробиця доїжджала клієнту
        // сирим українським реченням незалежно від мови інтерфейсу.
        Assert.NotNull(thrown.Details);
        Assert.Equal("err.ECR-PRJ-0409.projectCodeTaken", thrown.Details!["messageKey"]);
        Assert.Equal(code, thrown.Details["code"]);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
