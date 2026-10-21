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
