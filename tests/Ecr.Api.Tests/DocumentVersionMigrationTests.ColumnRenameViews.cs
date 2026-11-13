// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.ColumnRenameViews.cs
using System.Net;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// V-5 (аудит 2026-11-10): перенос із мапою перейменування колонки (<c>C2 → C2X</c>) зберігає пару в
/// <c>cfg.ColumnRename</c>, а аліаси вшиваються в текст стабільної вʼюхи <c>rpt.v_&lt;шаблон&gt;_&lt;аркуш&gt;_&lt;таблиця&gt;</c>
/// лише при генерації. Тому після коміту перенос ставить фонову задачу <c>ReportViewsTask</c> (F13 RC #872 P2-1: не
/// в запиті) — інакше колонка зі старим кодом для перенесеного проєкту NULL до перезапуску чи наступної публікації.
/// </summary>
/// <remarks>
/// Червоне до фіксу (виклик <c>MigrationReportViews.EnqueueAsync</c> у <c>MigrateDocumentVersionHandler</c> прибрано):
/// <c>[C2_…]</c> після переносу — NULL (вʼюха читає лише Id колонки старої версії, а значення вже на новому Id).
/// Постановку й порядок (після перерахунку) перевіряє <c>DocumentVersionMigrationTests.PostCommitTail</c>.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "RPT-STABLE")]
    [Trait("Finding", "V-5")]
    public async Task Column_rename_migration_regenerates_stable_view_so_old_code_still_reads_the_value()
    {
        var s = await ArrangeAsync(Target.ColumnRenamed).ConfigureAwait(true);
        string stable;
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            // Вихідна версія в будівнику - чернетка; стабільні вʼюхи беруть лише Published/Deprecated (як у бойовій базі).
            var source = await db.TemplateVersions.SingleAsync(v => v.Id == s.Doc.TemplateVersionId).ConfigureAwait(true);
            if (source.Status == TemplateVersionStatus.Draft)
            {
                source.Publish(1, DateTime.UtcNow);
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            var generator = new Ecr.Infrastructure.Reporting.ReportViewGenerator(db);
            await generator.GenerateAsync(s.Doc.TemplateVersionId, CancellationToken.None).ConfigureAwait(true);
            await generator.GenerateAsync(s.TargetVersionId, CancellationToken.None).ConfigureAwait(true);

            var templateCode = await db.Templates.AsNoTracking().Where(t => t.Id == s.Doc.TemplateId).Select(t => t.Code).SingleAsync().ConfigureAwait(true);
            stable = $"v_{templateCode}_{s.Doc.SheetCode}_{s.SourceTableCode}";
        }

        // Контроль ДО переносу: старий код читає значення проєкту (воно ще на колонці вихідної версії).
        Assert.Equal(42m, await StableValueAsync(stable, $"C2_{s.Tag}", s.Doc.ProjectId).ConfigureAwait(true));

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var response = await PostWithRenamesAsync(client, s, dryRun: false, ColumnOnlyRenames(s)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        // ⛔ Предмет тесту: фонова задача ReportViewsTask (поставлена переносом) перегенерувала вʼюхи — звіт SSRS на старому
        // коді колонки бачить перенесене значення без перезапуску застосунку. Задачу виконує справжня черга тестового хоста.
        decimal? oldCode = null;
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline && oldCode != 42m)
        {
            oldCode = await StableValueAsync(stable, $"C2_{s.Tag}", s.Doc.ProjectId).ConfigureAwait(true);
            if (oldCode != 42m)
            {
                await Task.Delay(500).ConfigureAwait(true);
            }
        }

        Assert.True(oldCode == 42m, $"[C2_{s.Tag}] після переносу: {oldCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}; {app.ErrorsText}");
        Assert.Equal(42m, await StableValueAsync(stable, $"C2X_{s.Tag}", s.Doc.ProjectId).ConfigureAwait(true));
    }

    /// <summary>Найбільше значення колонки стабільної вʼюхи для рядків проєкту; <c>null</c> — жодного значення.</summary>
    private async Task<decimal?> StableValueAsync(string view, string column, int projectId)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // імена вʼюхи й колонки складено з кодів шаблону тесту, не з вводу користувача
        command.CommandText = "SELECT MAX(TRY_CAST([" + column + "] AS decimal(38, 12))) FROM rpt.[" + view + "] WHERE _ProjectId = @p";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("@p", projectId);
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
