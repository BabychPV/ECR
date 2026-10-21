// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Marker.cs
using System.Net;
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Б1 (P1-1/P1-2): маркер «міграція в процесі» (<c>migratingTo</c> у <c>doc.Project</c>) — читачі й писарі проєкту отримують
/// 409 ECR-DOC-4091, доки діапазони комірок частково перенесені; маркер знімається разом з перемиканням версії.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>options.Filters.AddService&lt;MigrationMarkerFilter&gt;()</c> — перший тест червоніє (читач
/// отримує 200 з напівпереробленою сіткою); прибрати перевірку <c>migrating &amp;&amp; !exclusive</c> у
/// <c>SheetEditGate.EnterStructureAsync</c> — червоніють перевірки писаря в обох; прибрати очищення маркера з
/// <c>FinishSql</c> — червоніє перевірка «маркер знято».
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Під_час_переносу_читач_і_писар_отримують_409_а_після_завершення_маркер_знято_і_читання_працює()
    {
        var s = await ArrangeAsync(Target.OnlyLabels, extraPermission: "Document.View").ConfigureAwait(true);
        await BulkInsertAsync(s, 4_000).ConfigureAwait(true);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVersionMigrationStore>(sp => new DocumentVersionMigrationStore(sp.GetRequiredService<EcrDbContext>())
            {
                RangeRows = 1_000,
                RangeObserver = stat =>
                {
                    if (stat.Kind == "cells" && stat.Index == 1)
                    {
                        // Два діапазони вже зафіксовано, решта — ні: саме тут читач бачив би напівстан.
                        reached.TrySetResult();
                        release.Task.Wait(TimeSpan.FromSeconds(90));
                    }
                },
            })));
        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var started = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var jobId = JsonDocument.Parse(await started.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("jobId").GetString()!;
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(true);

        try
        {
            // Маркер зафіксовано, версія проєкту ще стара, частина комірок уже на цільових колонках.
            Assert.True(await MarkerPresentAsync(s).ConfigureAwait(true), "маркер «migratingTo» не виставлено");
            Assert.Equal(s.Doc.TemplateVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
            Assert.True(CountMovedCells(s) > 0, "жоден діапазон ще не зафіксовано — тест нічого не довів");

            // ⛔ P1-1: читач (RCSI, окреме з'єднання) НЕ бачить напівстану — дістає 409 міграції (кеш маркерів оновлюється ≤ 2 с).
            var url = new Uri($"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}", UriKind.Relative);
            HttpResponseMessage? read = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                read?.Dispose();
                read = await client.GetAsync(url).ConfigureAwait(true);
                if (read.StatusCode == HttpStatusCode.Conflict)
                {
                    break;
                }

                await Task.Delay(300).ConfigureAwait(true);
            }

            var body = await read!.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(read.StatusCode == HttpStatusCode.Conflict, $"читач під час переносу: {read.StatusCode}: {body[..Math.Min(300, body.Length)]}");
            var problem = JsonDocument.Parse(body).RootElement;
            Assert.Equal("ECR-DOC-4091", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-DOC-4091.structureChanging", problem.GetProperty("messageKey").GetString());

            // ⛔ P2-A: читачі DocumentsController (маршрут `{id}`) під маркером теж отримують 409, а не напівстан.
            foreach (var other in new[]
                     {
                         $"/api/v1/documents/{s.Doc.DocumentId}",
                         $"/api/v1/documents/{s.Doc.DocumentId}/header",
                         $"/api/v1/documents/{s.Doc.DocumentId}/tables",

                         // P2-N1: префікс шляху охоплює й контролери з іншим ім'ям (версії, порівняння, історія процесу), а query
                         // `documentId` - звіти/аудит комірок.
                         $"/api/v1/documents/{s.Doc.DocumentId}/versions",
                         $"/api/v1/documents/{s.Doc.DocumentId}/workflow/history",
                         $"/api/v1/audit/cells?documentId={s.Doc.DocumentId}",
                     })
            {
                using var otherRead = await client.GetAsync(new Uri(other, UriKind.Relative)).ConfigureAwait(true);
                Assert.True(otherRead.StatusCode == HttpStatusCode.Conflict, $"{other}: {otherRead.StatusCode}");
            }

            // ⛔ P1-2: писар (спільна структура документа — перша дія кожного запису) відмовляє тим самим кодом.
            await AssertWriterRefusedAsync(s).ConfigureAwait(true);
        }
        finally
        {
            release.TrySetResult();
        }

        var job = await WaitJobAsync(client, jobId, baseApp).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{baseApp.ErrorsText}");

        // Маркер знято тією самою командою, що перемкнула версію.
        Assert.False(await MarkerPresentAsync(s).ConfigureAwait(true), "маркер лишився після завершення");
        Assert.Equal(s.TargetVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));

        // Читання знову працює (кеш маркерів оновлюється ≤ 2 с).
        var okUrl = new Uri($"/api/v1/documents/{s.Doc.DocumentId}/tables/{s.Doc.TableInstanceId}", UriKind.Relative);
        HttpStatusCode last = default;
        var okDeadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < okDeadline)
        {
            using var again = await client.GetAsync(okUrl).ConfigureAwait(true);
            last = again.StatusCode;
            if (last == HttpStatusCode.OK)
            {
                break;
            }

            await Task.Delay(300).ConfigureAwait(true);
        }

        Assert.Equal(HttpStatusCode.OK, last);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Обрив_переносу_лишає_маркер_писар_заблокований_а_повтор_доводить_до_кінця_і_знімає_маркер()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);
        await BulkInsertAsync(s, 4_000).ConfigureAwait(true);
        var before = await AggregatesAsync(s).ConfigureAwait(true);

        // ── Перший запуск: збій після третього діапазону ──
        using (var baseApp = new EcrApiFactory(sql))
        using (var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVersionMigrationStore>(sp => new DocumentVersionMigrationStore(sp.GetRequiredService<EcrDbContext>())
            {
                RangeRows = 1_000,
                RangeObserver = stat =>
                {
                    if (stat is { Kind: "cells", Index: 2 })
                    {
                        throw new Ecr.Application.Errors.BusinessRuleException(Ecr.Domain.Errors.ErrorCodes.JobStateConflict, "імітований обрив між діапазонами");
                    }
                },
            }))))
        using (var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true))
        {
            var started = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
            var jobId = JsonDocument.Parse(await started.Content.ReadAsStringAsync().ConfigureAwait(true))
                .RootElement.GetProperty("jobId").GetString()!;
            var failed = await WaitJobAsync(client, jobId, baseApp).ConfigureAwait(true);
            Assert.True(failed.GetProperty("state").GetString() == "Failed", $"{failed.GetRawText()}\n{baseApp.ErrorsText}");
        }

        // Версія стара, маркер лишився, частина комірок уже перенесена; писар заблокований (P1-2).
        Assert.Equal(s.Doc.TemplateVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.True(await MarkerPresentAsync(s).ConfigureAwait(true), "після обриву маркер мусить лишитися");
        Assert.True(CountMovedCells(s) > 0, "обрив стався до першого коміту — тест нічого не довів");
        await AssertWriterRefusedAsync(s).ConfigureAwait(true);

        // ── Повтор: той самий перенос (звичайне сховище) доводить до кінця й знімає маркер ──
        using (var baseApp = new EcrApiFactory(sql))
        using (var client = await SignedInAsync(baseApp, s.UserName, baseApp).ConfigureAwait(true))
        {
            var started = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
            var jobId = JsonDocument.Parse(await started.Content.ReadAsStringAsync().ConfigureAwait(true))
                .RootElement.GetProperty("jobId").GetString()!;
            var job = await WaitJobAsync(client, jobId, baseApp).ConfigureAwait(true);
            Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"повтор: {job.GetRawText()}\n{baseApp.ErrorsText}");
        }

        Assert.False(await MarkerPresentAsync(s).ConfigureAwait(true), "маркер лишився після повтору");
        Assert.Equal(s.TargetVersionId, await ProjectVersionAsync(s).ConfigureAwait(true));
        Assert.Equal(before, await AggregatesAsync(s).ConfigureAwait(true));
        Assert.Equal(0, await ScalarAsync(
            "SELECT COUNT(*) FROM doc.CellValue cv JOIN doc.TableRow r ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId "
            + "WHERE cv.PeriodKey = @pk AND r.TableInstanceId = @ti AND cv.TableDefId = @oldTd", s).ConfigureAwait(true));
    }

    /// <summary>Спільне блокування структури документа (як у кожного писаря) відмовляє 409 ECR-DOC-4091 при маркері.</summary>
    private async Task AssertWriterRefusedAsync(Scenario s)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);
        var refused = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new SheetEditGate(db).EnterStructureAsync(s.Doc.DocumentId, exclusive: false, CancellationToken.None)).ConfigureAwait(true);
        Assert.Equal("ECR-DOC-4091", refused.ErrorCode);
        await tx.RollbackAsync().ConfigureAwait(true);
    }

    private async Task<bool> MarkerPresentAsync(Scenario s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN ExternalSettingsJson LIKE '%\"migratingTo\"%' THEN 1 ELSE 0 END FROM doc.Project WHERE Id = @p";
        command.Parameters.Add(new SqlParameter("@p", s.Doc.ProjectId));
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }
}
