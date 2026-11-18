using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>B167: імпорт CSV записів довідника — запис <c>RegistryValue</c> поза ендпоінтом записів (правило 429).</summary>
public sealed partial class AdminDuplicateRequestHttpTests
{
    /// <summary>
    /// e4: <c>POST /registries/{code}/entries/import</c> — файл, що вперше заповнює порожнє поле наявного запису, — двічі одночасно:
    /// обидва <c>200</c>; один застосував (<c>updated=1</c>), другий — той самий звіт, що й послідовний повтор (<c>applied=false,
    /// unchanged=1</c>); значення одне. Доти переможений отримував загальний <c>409 concurrentChange</c> (значення прочитано до
    /// транзакції, вставку відбиває <c>UQ_RegistryValue</c>), хоча послідовний повтор того самого файлу — <c>200</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B167")]
    public async Task e4_Імпорт_CSV_двічі_одночасно_обидва_200_і_звіт_як_у_послідовного_повтору()
    {
        var (one, two) = await SignInTwiceAsync(["Registry.View", "Registry.EditData"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var (registryId, code) = await NewRegistryAsync(withField: "NOTE").ConfigureAwait(true);
        var entries = $"/api/v1/registries/{code}/entries";
        var import = $"/api/v1/registries/{code}/entries/import";

        async Task<long> NewEntryAsync(string entryCode)
        {
            var body = new
            {
                id = (long?)null,
                registryDefId = registryId,
                code = entryCode,
                display = new { values = new { en = entryCode } },
                parentEntryId = (long?)null,
                values = new Dictionary<string, object?>(),
            };
            var created = await SendAsync(first, HttpMethod.Post, entries, body).ConfigureAwait(true);
            Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
            return created.Json.GetProperty("id").GetInt64();
        }

        // Послідовно: перший файл застосовано, повтор того самого — 200 без змін.
        _ = await NewEntryAsync("E1");
        var applied = await ImportAsync(first, import, "E1", null).ConfigureAwait(true);
        var repeated = await ImportAsync(first, import, "E1", null).ConfigureAwait(true);

        Assert.True(applied.Status == HttpStatusCode.OK, $"{applied}\n{App.ErrorsText}");
        AssertReport(applied, applied: true, updated: 1, unchanged: 0);
        Assert.True(repeated.Status == HttpStatusCode.OK, $"{repeated}\n{App.ErrorsText}");
        AssertReport(repeated, applied: false, updated: 0, unchanged: 1);

        // Одночасно. ⚠ Вікно гонки — від читання значень до коміту, і зустріч на вході конвеєра не завжди його перекриває
        // (без фіксу проба червоніла не в кожному прогоні): кілька спроб, кожна на своєму записі, і кожна мусить дати ту саму
        // відповідь, що й послідовно.
        for (var attempt = 1; attempt <= RaceAttempts; attempt++)
        {
            var entryCode = $"R{attempt}";
            var entryId = await NewEntryAsync(entryCode);
            var replies = await RaceAsync(
                $"entries-import#{attempt}",
                meet => ImportAsync(first, import, entryCode, meet),
                meet => ImportAsync(second, import, entryCode, meet)).ConfigureAwait(true);

            AssertNoServerError(replies);
            Assert.True(
                replies.All(r => r.Status == HttpStatusCode.OK),
                $"спроба {attempt}: очікувались обидва 200:\n{replies[0]}\n{replies[1]}\n{App.ErrorsText}");
            AssertReport(Assert.Single(replies, r => r.Json.GetProperty("applied").GetBoolean()), applied: true, updated: 1, unchanged: 0);
            AssertReport(Assert.Single(replies, r => !r.Json.GetProperty("applied").GetBoolean()), applied: false, updated: 0, unchanged: 1);

            await using var db = Db();
            Assert.Equal(1, await db.RegistryValues.CountAsync(v => v.RegistryEntryId == entryId).ConfigureAwait(true));
        }
    }

    /// <summary>Скільки разів e4 зіштовхує два імпорти (кожен раз — новий запис).</summary>
    private const int RaceAttempts = 6;

    private static void AssertReport(Reply reply, bool applied, int updated, int unchanged)
    {
        Assert.Equal(applied, reply.Json.GetProperty("applied").GetBoolean());
        Assert.Equal(0, reply.Json.GetProperty("added").GetInt32());
        Assert.Equal(updated, reply.Json.GetProperty("updated").GetInt32());
        Assert.Equal(unchanged, reply.Json.GetProperty("unchanged").GetInt32());
        Assert.Equal(0, reply.Json.GetProperty("errors").GetArrayLength());
    }

    private static async Task<Reply> ImportAsync(HttpClient client, string url, string entryCode, string? meet)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative));
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes($"code,NOTE\r\n{entryCode},B167\r\n"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        var form = new MultipartFormDataContent { { file, "file", "entries.csv" } };
        request.Content = form;
        if (meet is not null)
        {
            request.Headers.Add(MeetHeader, meet);
        }

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (response.IsSuccessStatusCode || text.Length == 0 || text[0] != '{')
        {
            return new Reply(response.StatusCode, null, null, text);
        }

        var root = System.Text.Json.JsonDocument.Parse(text).RootElement;
        return new Reply(response.StatusCode, Text(root, "errorCode"), Text(root, "messageKey"), text);
    }
}
