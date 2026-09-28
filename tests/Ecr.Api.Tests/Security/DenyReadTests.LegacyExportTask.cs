// tests/Ecr.Api.Tests/Security/DenyReadTests.LegacyExportTask.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S6: завдання експорту без меж читання (поставлене до S6) — закрито за
/// замовчуванням, а не «експортувати все».
/// </summary>
/// <remarks>
/// ⛔ До фіксу завдання без <c>hiddenTableDefIds</c>/<c>hiddenColumnDefIds</c>
/// експортувалось без фільтра: заборонена замовникові колонка їхала у файлі.
/// Завдання ставиться прямо в чергу — тим самим портом, що й
/// <c>ExportDocumentHandler</c>, але з тілом старої форми.
/// </remarks>
public sealed partial class DenyReadTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Завдання_без_меж_рахує_їх_від_імені_замовника()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        // Замовник названий, меж немає.
        var exportId = Guid.NewGuid().ToString("N");
        var jobId = await EnqueueRawExportAsync(app, $$"""
            {"documentId":{{s.Doc.DocumentId}},"options":{"includeFormulas":false,"includeStyles":false,"language":"en","periodKey":{{s.Doc.PeriodKey.Value}}},"exportId":"{{exportId}}","format":"json","requestedByUserId":{{s.ReaderId}}}
            """, s.ReaderId).ConfigureAwait(true);

        var job = await WaitJobAsync(app, reader, jobId).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{app.ErrorsText}");

        var (status, text) = await GetAsync(reader, $"/api/v1/documents/{s.Doc.DocumentId}/export/{exportId}").ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {text}\n{app.ErrorsText}");

        Assert.Contains(Digits(Visible), text, StringComparison.Ordinal);
        foreach (var secret in new[]
                 {
                     Digits(DeniedColumn), Digits(DeniedTable), Digits(DeniedSheet),
                     s.ColumnCodes[2], s.DeniedTable.TableCode, s.DeniedSheetTable.TableCode,
                 })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Завдання_до_S6_без_замовника_провалюється_а_не_експортує_все()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        // Тіло рівно тієї форми, що до S6: ні меж, ні замовника.
        var exportId = Guid.NewGuid().ToString("N");
        var jobId = await EnqueueRawExportAsync(app, $$"""
            {"documentId":{{s.Doc.DocumentId}},"options":{"includeFormulas":false,"includeStyles":false,"language":"en","periodKey":{{s.Doc.PeriodKey.Value}}},"exportId":"{{exportId}}","format":"json"}
            """, s.ReaderId).ConfigureAwait(true);

        var job = await WaitJobAsync(app, reader, jobId).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Failed", $"задача: {job.GetRawText()}\n{app.ErrorsText}");

        var (status, body) = await GetAsync(reader, $"/api/v1/documents/{s.Doc.DocumentId}/export/{exportId}").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.DoesNotContain(Digits(DeniedColumn), body, StringComparison.Ordinal);
    }

    /// <summary>Ставить задачу експорту з сирим тілом — так, як воно лежить у черзі.</summary>
    private static async Task<string> EnqueueRawExportAsync(EcrApiFactory app, string json, int createdBy)
    {
        using var scope = app.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>();

        using var payload = JsonDocument.Parse(json);
        return await jobs
            .EnqueueAsync<IExcelExportJob>(payload.RootElement.Clone(), CancellationToken.None, createdBy)
            .ConfigureAwait(false);
    }

    private static async Task<JsonElement> WaitJobAsync(EcrApiFactory app, HttpClient client, string jobId)
    {
        JsonElement job = default;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            job = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(false);
            if (job.GetProperty("state").GetString() is not ("Queued" or "Running"))
            {
                return job;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        Assert.Fail($"задача не завершилась за 60 с: {job.GetRawText()}\n{app.ErrorsText}");
        return job;
    }
}
