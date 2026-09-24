// tests/Ecr.Infrastructure.Tests/Persistence/DocumentKeyNumberingTests.cs
using Ecr.Application.Documents;
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
/// `R-17`: номер у ключі документа лише зростає — ключ видаленого чи
/// перейменованого документа не видається повторно.
/// </summary>
/// <remarks>
/// ⛔ Що ламалося, живцем на стенді: номер рахувався як <c>COUNT + 1</c>.
/// Документ <c>P5-V3-0005</c> видалили — і наступний створений отримав той
/// самий <c>P5-V3-0005</c>: один ключ в аудиті, у назвах експортів і в
/// листуванні означав тепер два різні документи.
///
/// ⛔ Мутації, на яких тести зобов'язані впасти: повернути <c>used = count</c>
/// у <c>DocumentStore.NextBusinessKeyAsync</c> (усі три); прибрати гілку
/// журналу з <c>HighestIssuedNumberAsync</c> (перший і третій).
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentKeyNumberingTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-17")]
    public async Task Ключ_видаленого_документа_не_видається_знову()
    {
        var projectId = await ArrangeProjectAsync();

        await AddDocumentsAsync(projectId, 1, 2);

        // Документ №3 видалено — у базі його вже немає, лишився слід у журналі.
        await AddSecurityEventAsync(
            DeleteDocumentHandler.DeletedEventType,
            $$"""{"documentId":1,"projectId":{{projectId}},"businessKey":"P{{projectId}}-V1-0003","cells":0}""");

        var next = await NextKeyAsync(projectId);

        // ⛔ До виправлення: `COUNT(2) + 1` → знову `0003`.
        Assert.Equal($"P{projectId}-V1-0004", next);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-17")]
    public async Task Дірка_від_видалення_посередині_не_заповнюється()
    {
        var projectId = await ArrangeProjectAsync();

        // Живі №1 і №5: кількість — 2, найбільший виданий — 5.
        await AddDocumentsAsync(projectId, 1, 5);

        Assert.Equal($"P{projectId}-V1-0006", await NextKeyAsync(projectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-17")]
    public async Task Старий_ключ_перейменованого_документа_теж_зайнятий_назавжди()
    {
        var projectId = await ArrangeProjectAsync();

        await AddDocumentsAsync(projectId, 1);

        // №5 перейменували на ключ людини: живих два (№1 і CUSTOM), але №5 уже видавався.
        await using (var db = Context())
        {
            db.Documents.Add(new Document(projectId, $"CUSTOM-{_tag}", 1, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        await AddSecurityEventAsync(
            ChangeDocumentKeyHandler.EventType,
            $$"""{"documentId":5,"projectId":{{projectId}},"oldKey":"P{{projectId}}-V1-0005","newKey":"CUSTOM-{{_tag}}","reason":"r"}""");

        Assert.Equal($"P{projectId}-V1-0006", await NextKeyAsync(projectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-17")]
    public async Task Слід_ЧУЖОГО_проєкту_нумерацію_не_зсуває()
    {
        var projectId = await ArrangeProjectAsync();
        var other = projectId + 100_000;

        await AddDocumentsAsync(projectId, 1);
        await AddSecurityEventAsync(
            DeleteDocumentHandler.DeletedEventType,
            $$"""{"documentId":9,"projectId":{{other}},"businessKey":"P{{projectId}}-V1-0042","cells":0}""");

        Assert.Equal($"P{projectId}-V1-0002", await NextKeyAsync(projectId));
    }

    [Theory]
    [InlineData("P5-V3-0007", "P5-V", 7)]
    [InlineData("P5-V12-0100", "P5-V", 100)]
    [InlineData("P51-V3-0007", "P5-V", null)]
    [InlineData("P5-V3x-0007", "P5-V", null)]
    [InlineData("P5-V3-", "P5-V", null)]
    [InlineData("CUSTOM", "P5-V", null)]
    [InlineData(null, "P5-V", null)]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "R-17")]
    public void Номер_читається_лише_з_ключа_свого_шаблону(string? key, string prefix, int? expected)
        => Assert.Equal(expected, DocumentStore.NumberOf(key, prefix));

    private async Task<string> NextKeyAsync(int projectId)
    {
        await using var db = Context();

        return await new DocumentStore(db).NextBusinessKeyAsync(projectId, 1, CancellationToken.None);
    }

    private async Task AddDocumentsAsync(int projectId, params int[] numbers)
    {
        await using var db = Context();

        foreach (var number in numbers)
        {
            db.Documents.Add(new Document(projectId, $"P{projectId}-V1-{number:D4}", 1, DateTime.UtcNow));
        }

        await db.SaveChangesAsync();
    }

    private async Task AddSecurityEventAsync(string eventType, string detailsJson)
    {
        await using var db = Context();

        var userId = await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO aud.SecurityEvent (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
            VALUES (SYSUTCDATETIME(), {eventType}, NULL, NULL, {detailsJson}, {userId}, {_tag});
            """);
    }

    /// <summary>Шаблон → версія → проєкт; повертає ідентифікатор проєкту.</summary>
    private async Task<int> ArrangeProjectAsync()
    {
        await using var db = Context();

        var template = new Template(EcrCode.Create($"NUM_{_tag}"), Text("Template"), 1, DateTime.UtcNow);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, DateTime.UtcNow);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var project = new Project(
            EcrCode.Create($"NUMPRJ_{_tag}"), Text("Project"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            version.Id, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        return project.Id;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
