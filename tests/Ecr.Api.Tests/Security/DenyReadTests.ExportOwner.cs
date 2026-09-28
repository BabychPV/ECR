// tests/Ecr.Api.Tests/Security/DenyReadTests.ExportOwner.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S6: готовий експорт завантажує лише той, хто його замовив.
/// </summary>
/// <remarks>
/// ⛔ До фіксу <c>DownloadExportHandler</c> перевіряв лише видимість документа:
/// файл, побудований у межах читання одного користувача, забирав будь-хто з
/// видимістю документа, хто знав <c>exportId</c>. Тут навпаки: замовник —
/// <c>reader</c> (із заборонами), а чужий — <c>plain</c>, який документ бачить.
/// Отже 404 тут — саме про власника, а не про видимість.
/// </remarks>
public sealed partial class DenyReadTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Готовий_експорт_завантажує_лише_замовник_чужий_exportId_як_неіснуючий()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true);
        using var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true);

        var exportId = await OrderExportAsync(app, reader, s).ConfigureAwait(true);
        string Url(string id) => $"/api/v1/documents/{s.Doc.DocumentId}/export/{id}";

        // Замовник — файл.
        var (own, ownBody) = await GetAsync(reader, Url(exportId)).ConfigureAwait(true);
        Assert.True(own == HttpStatusCode.OK, $"{own}: {ownBody}\n{app.ErrorsText}");

        // Інший користувач із видимістю документа — 404.
        var (foreign, foreignBody) = await GetAsync(plain, Url(exportId)).ConfigureAwait(true);
        Assert.True(foreign == HttpStatusCode.NotFound, $"{foreign}: {foreignBody}\n{app.ErrorsText}");

        // Неіснуючий — та сама 404, та сама форма: відповідь не каже, що файл є.
        var missingId = Guid.NewGuid().ToString("N");
        var (missing, missingBody) = await GetAsync(plain, Url(missingId)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, missing);

        var a = JsonDocument.Parse(foreignBody).RootElement;
        var b = JsonDocument.Parse(missingBody).RootElement;
        foreach (var field in new[] { "errorCode", "messageKey", "detail", "title", "status" })
        {
            Assert.Equal(b.GetProperty(field).GetRawText(), a.GetProperty(field).GetRawText());
        }
    }

    /// <summary>Замовляє експорт xlsx і чекає на його <c>exportId</c>.</summary>
    private static async Task<string> OrderExportAsync(EcrApiFactory app, HttpClient client, Scenario s)
    {
        var start = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/export", UriKind.Relative),
            new { includeFormulas = false, includeStyles = false, language = "en", periodKey = s.Doc.PeriodKey.Value, format = "xlsx" })
            .ConfigureAwait(false);
        Assert.True(start.StatusCode == HttpStatusCode.Accepted, $"експорт: {start.StatusCode}: {app.ErrorsText}");
        var jobId = (await start.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false))
            .GetProperty("jobId").GetString()!;

        JsonElement job = default;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            job = await client.GetFromJsonAsync<JsonElement>(
                new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(false);
            if (job.GetProperty("state").GetString() is not ("Queued" or "Running"))
            {
                break;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{app.ErrorsText}");
        return job.GetProperty("message").GetString()!;
    }
}
