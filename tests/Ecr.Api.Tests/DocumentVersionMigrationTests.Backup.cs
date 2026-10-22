// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Backup.cs
using System.Net;
using System.Net.Http.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Рішення людини 21.10 («б»): підтвердження бекапу (<c>backupConfirmation</c>) обов'язкове лише для великих переносів
/// (від <see cref="MigrateDocumentVersionHandler.BackupConfirmationThreshold"/> значень); його пишуть у журнал безпеки.
/// </summary>
/// <remarks>
/// Поріг у тестах опускається до 1 (статична властивість; тести цього класу виконуються послідовно). Мутація: прибрати
/// перевірку в <c>MigrateDocumentVersionHandler</c> - перший тест червоніє; прибрати запис у деталі подій - другий.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Великий_перенос_без_підтвердження_бекапу_422_і_нічого_не_записано()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);
        var saved = MigrateDocumentVersionHandler.BackupConfirmationThreshold;
        MigrateDocumentVersionHandler.BackupConfirmationThreshold = 1;
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, s.UserName, app).ConfigureAwait(true);

            var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
            await AssertProblemAsync(
                response, HttpStatusCode.UnprocessableEntity, "ECR-SCHM-0422", "err.ECR-SCHM-0422.migrateBackupRequired").ConfigureAwait(true);

            // Відмова ДО маркера: нічого не записано, проєкт не заблоковано.
            Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
            Assert.False(await MarkerPresentAsync(s).ConfigureAwait(true));
        }
        finally
        {
            MigrateDocumentVersionHandler.BackupConfirmationThreshold = saved;
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Фонова_задача_несе_підтвердження_бекапу_до_обробника_і_без_нього_великий_перенос_Failed_422()
    {
        const string confirmation = "F:\\ecr-backup\\land-async.bak 2026-10-21T16:00Z";
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var saved = MigrateDocumentVersionHandler.BackupConfirmationThreshold;
        MigrateDocumentVersionHandler.BackupConfirmationThreshold = 1;
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, s.UserName, app).ConfigureAwait(true);
            var url = new Uri($"/api/v1/documents/{s.Doc.DocumentId}/migrate-version", UriKind.Relative);

            // Без підтвердження: постановка приймається (план у задачі), задача завершується Failed з ECR-SCHM-0422.
            var without = await client.PostAsJsonAsync(
                url, new { targetVersionId = s.TargetVersionId, mode = "Safe", dryRun = false, async = true }).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, without.StatusCode);
            var withoutId = System.Text.Json.JsonDocument.Parse(await without.Content.ReadAsStringAsync().ConfigureAwait(true))
                .RootElement.GetProperty("jobId").GetString()!;
            var failed = await WaitJobAsync(client, withoutId, app).ConfigureAwait(true);
            Assert.True(failed.GetProperty("state").GetString() == "Failed", failed.GetRawText());
            Assert.Equal("ECR-SCHM-0422", failed.GetProperty("errorCode").GetString());
            Assert.Equal(s.Doc.TemplateVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));

            // З підтвердженням у тілі: значення проходить запит -> задачу -> обробник і потрапляє в журнал.
            var with = await client.PostAsJsonAsync(
                url, new { targetVersionId = s.TargetVersionId, mode = "Safe", dryRun = false, async = true, backupConfirmation = confirmation })
                .ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, with.StatusCode);
            var withId = System.Text.Json.JsonDocument.Parse(await with.Content.ReadAsStringAsync().ConfigureAwait(true))
                .RootElement.GetProperty("jobId").GetString()!;
            var done = await WaitJobAsync(client, withId, app).ConfigureAwait(true);
            Assert.True(done.GetProperty("state").GetString() == "Succeeded", $"{done.GetRawText()}\n{app.ErrorsText}");
        }
        finally
        {
            MigrateDocumentVersionHandler.BackupConfirmationThreshold = saved;
        }

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        foreach (var type in new[] { MigrateDocumentVersionHandler.StartedEventType, MigrateDocumentVersionHandler.EventType })
        {
            var details = await db.Database
                .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = {type} AND ChangedByUserId = {s.UserId}")
                .ToListAsync().ConfigureAwait(true);
            Assert.Single(details);
            Assert.Contains("land-async.bak", details[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task З_підтвердженням_бекапу_перенос_іде_а_підтвердження_потрапляє_в_журнал_початку_і_завершення()
    {
        const string confirmation = "F:\\ecr-backup\\land-pre-migrate.bak 2026-10-21T15:00Z";
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        var saved = MigrateDocumentVersionHandler.BackupConfirmationThreshold;
        MigrateDocumentVersionHandler.BackupConfirmationThreshold = 1;
        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await SignedInAsync(app, s.UserName, app).ConfigureAwait(true);

            var response = await client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{s.Doc.DocumentId}/migrate-version", UriKind.Relative),
                new { targetVersionId = s.TargetVersionId, mode = "Safe", dryRun = false, backupConfirmation = confirmation })
                .ConfigureAwait(true);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(true)}");
        }
        finally
        {
            MigrateDocumentVersionHandler.BackupConfirmationThreshold = saved;
        }

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        foreach (var type in new[] { MigrateDocumentVersionHandler.StartedEventType, MigrateDocumentVersionHandler.EventType })
        {
            var details = await db.Database
                .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = {type} AND ChangedByUserId = {s.UserId}")
                .ToListAsync().ConfigureAwait(true);
            Assert.Single(details);
            Assert.Contains("land-pre-migrate.bak", details[0], StringComparison.Ordinal);
        }
    }
}
