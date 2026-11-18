using System.Net;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>B167 (рішення координатора 17.11, п. 3): правка запису довідника під гонкою — відповідь послідовного повтору.</summary>
public sealed partial class AdminDuplicateRequestHttpTests
{
    /// <summary>
    /// e6: <c>POST /registries/{code}/entries</c> — правка наявного запису, що вперше заповнює порожнє поле (шлях d3), — двічі
    /// одночасно: обидва <c>200</c>, як і послідовний повтор (upsert без токена версії), значення одне. Доти переможений отримував
    /// загальний <c>409 concurrentChange</c> (B158d d3 його допускав). Кілька зіткнень на прогін, кожне на своєму записі: вікно
    /// гонки — від читання значень до коміту, зустріч на вході конвеєра перекриває його не щоразу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B167")]
    public async Task e6_Правка_запису_двічі_одночасно_обидва_200_як_послідовний_повтор()
    {
        var (one, two) = await SignInTwiceAsync(["Registry.View", "Registry.EditData"]).ConfigureAwait(true);
        using var first = one;
        using var second = two;

        var (registryId, code) = await NewRegistryAsync(withField: "NOTE").ConfigureAwait(true);
        var url = $"/api/v1/registries/{code}/entries";

        object Body(long? id, string entryCode, IReadOnlyDictionary<string, object?> values) => new
        {
            id,
            registryDefId = registryId,
            code = entryCode,
            display = new { values = new { en = entryCode } },
            parentEntryId = (long?)null,
            values,
        };

        var empty = new Dictionary<string, object?>();
        var note = new Dictionary<string, object?> { ["NOTE"] = "B167" };

        async Task<long> NewEntryAsync(string entryCode)
        {
            var created = await SendAsync(first, HttpMethod.Post, url, Body(null, entryCode, empty)).ConfigureAwait(true);
            Assert.True(created.Status == HttpStatusCode.Created, $"{created}\n{App.ErrorsText}");
            return created.Json.GetProperty("id").GetInt64();
        }

        // Послідовно: повтор тієї самої правки — той самий 200.
        var e1 = await NewEntryAsync("E1");
        var saved = await SendAsync(first, HttpMethod.Post, url, Body(e1, "E1", note)).ConfigureAwait(true);
        var repeated = await SendAsync(first, HttpMethod.Post, url, Body(e1, "E1", note)).ConfigureAwait(true);
        Assert.True(saved.Status == HttpStatusCode.OK, $"{saved}\n{App.ErrorsText}");
        Assert.True(repeated.Status == HttpStatusCode.OK, $"{repeated}\n{App.ErrorsText}");

        for (var attempt = 1; attempt <= RaceAttempts; attempt++)
        {
            var entryCode = $"U{attempt}";
            var entryId = await NewEntryAsync(entryCode);
            var replies = await RaceAsync(
                $"entry-upsert#{attempt}",
                meet => SendAsync(first, HttpMethod.Post, url, Body(entryId, entryCode, note), meet),
                meet => SendAsync(second, HttpMethod.Post, url, Body(entryId, entryCode, note), meet)).ConfigureAwait(true);

            AssertNoServerError(replies);
            Assert.True(
                replies.All(r => r.Status == repeated.Status),
                $"спроба {attempt}: очікувались обидва {(int)repeated.Status}:\n{replies[0]}\n{replies[1]}\n{App.ErrorsText}");
            Assert.All(replies, r => Assert.Equal(entryId, r.Json.GetProperty("id").GetInt64()));

            await using var db = Db();
            Assert.Equal(1, await db.RegistryValues.CountAsync(v => v.RegistryEntryId == entryId).ConfigureAwait(true));
        }
    }
}
