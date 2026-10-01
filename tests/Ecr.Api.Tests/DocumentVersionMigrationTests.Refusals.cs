// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Refusals.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Відмови переносу документа на іншу версію шаблону, які гайд тестувальника
/// (TESTER-SCENARIOS Н-Ж3) називає, а HTTP-тест досі не перевіряв: невідома
/// ціль, ціль = поточна версія, опублікована версія ЧУЖОГО шаблону й
/// архівований проєкт. Кожна — з ключем повідомлення і без змін у даних.
/// </summary>
/// <remarks>
/// Мутаційні докази (усі — у <c>src/Ecr.Application/Documents/VersionMigration/MigrateDocumentVersionHandler.cs</c>):
/// <list type="bullet">
/// <item><see cref="Невідома_цільова_версія_404_з_ключем"/>: у <c>RequireTargetAsync</c>
/// <c>"err.ECR-TMPL-0404.templateVersion"</c> → <c>"err.ECR-TMPL-0404.document"</c> — ключ не той.</item>
/// <item><see cref="Ціль_дорівнює_поточній_версії_422_migrateSameVersion"/>: у <c>SameVersion</c>
/// <c>"err.ECR-TMPL-0422.migrateSameVersion"</c> → <c>"err.ECR-TMPL-0422.migrateOtherTemplate"</c>.
/// (Поведінкова мутація однієї з двох перевірок «та сама версія» не червоніє:
/// друга, під блоком у транзакції, дублює її навмисно.)</item>
/// <item><see cref="Опублікована_версія_іншого_шаблону_422_migrateOtherTemplate"/>:
/// <c>if (sourceTemplate is null || sourceTemplate.Id != target.TemplateId)</c> →
/// <c>if (sourceTemplate is null)</c> — перенос іде далі й відмовляє (або проходить) з
/// іншої причини, ключа <c>migrateOtherTemplate</c> немає.</item>
/// <item><see cref="Архівований_проєкт_409_migrateProjectArchived_і_нічого_не_змінює"/>:
/// <c>project.Status == ProjectStatus.Archived || project.IsArchiving</c> →
/// <c>project.IsArchiving</c> — перенос «лише підписів» проходить, 200 замість 409.</item>
/// </list>
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Scenario", "Н-Ж3")]
    public async Task Невідома_цільова_версія_404_з_ключем()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostToAsync(client, s, int.MaxValue).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "ECR-TMPL-0404", "err.ECR-TMPL-0404.templateVersion").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Scenario", "Н-Ж3")]
    public async Task Ціль_дорівнює_поточній_версії_422_migrateSameVersion()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostToAsync(client, s, s.Doc.TemplateVersionId).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422", "err.ECR-TMPL-0422.migrateSameVersion").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Scenario", "Н-Ж3")]
    public async Task Опублікована_версія_іншого_шаблону_422_migrateOtherTemplate()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);

        // Окремий будівник — окремий шаблон; його цільова версія опублікована,
        // тож відмова може бути лише через «чужий шаблон», не через статус.
        var foreign = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        Assert.NotEqual(s.Doc.TemplateId, foreign.Doc.TemplateId);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostToAsync(client, s, foreign.TargetVersionId).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "ECR-TMPL-0422", "err.ECR-TMPL-0422.migrateOtherTemplate").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [Trait("Scenario", "Н-Ж3")]
    public async Task Архівований_проєкт_409_migrateProjectArchived_і_нічого_не_змінює()
    {
        // OnlyLabels: без архівації перенос пройшов би (лише підписи) — тож 409
        // може дати тільки стан проєкту.
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);

        // Архівація — у підготовці, прямо в базі (як ArchivedProjectRulesTests):
        // PeriodStateJob старту застосунку архівованих проєктів не чіпає.
        var now = new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var project = await db.Projects.Include(p => p.Periods)
                .FirstAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(true);
            project.Activate(now);
            foreach (var period in project.Periods)
            {
                period.AdvanceTo(PeriodState.Closed, now);
            }

            project.Archive(now);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "ECR-DOC-0409", "err.ECR-DOC-0409.migrateProjectArchived").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    private static Task<HttpResponseMessage> PostToAsync(HttpClient client, Scenario s, int targetVersionId)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId, mode = "Safe", dryRun = false });
}
