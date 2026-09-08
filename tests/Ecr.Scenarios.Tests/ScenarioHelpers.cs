using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// Три допоміжники спільні для всіх сценаріїв (директива §"Що зробити технічно" п.4).
/// </summary>
/// <remarks>
/// ⛔ Навмисно НІЧОГО, крім цих трьох: жодного <c>ProjectBuilder</c> чи
/// <c>TemplateBuilder</c>-подібного помічника, який ховав би послідовність
/// HTTP-викликів за одним зручним методом. Кожен сценарій сам виконує свій
/// ланцюжок викликів — саме це і відрізняє сценарій від демонстрації
/// (Правило 1, §3.2).
/// </remarks>
internal static class ScenarioHelpers
{
    /// <summary>
    /// Виконує <c>POST /api/v1/login/local</c> і повертає той самий клієнт,
    /// готовий до наступних запитів — <c>HttpClient</c> від
    /// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}.CreateClient()"/>
    /// зберігає cookie між запитами сам.
    /// </summary>
    /// <remarks>
    /// Падає з чітким повідомленням, якщо вхід не пройшов: виклик належить до
    /// приготування сценарію (вхід під заздалегідь заведеним користувачем), а
    /// не до кроку, який сценарій перевіряє, — тому мовчати про 401 тут не
    /// можна, інакше далі сценарій падає на геть іншому й незрозумілому кроці.
    /// </remarks>
    public static async Task<HttpClient> SignedInAsync(
        HttpClient client, string userName, string password, CancellationToken ct = default)
    {
        var response = await client
            .PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password }, ct)
            .ConfigureAwait(false);

        Assert.True(
            response.IsSuccessStatusCode,
            $"вхід '{userName}' не пройшов: {response.StatusCode}: " +
            await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

        return client;
    }

    /// <summary>
    /// Опитує <c>GET /api/v1/jobs/{jobId}</c>, поки стан не стане кінцевим
    /// (щось інше за <c>Queued</c>/<c>Running</c>) або не мине <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Перерахунок, експорт і збір — асинхронні (`ФВ-14.8`): сервер віддає
    /// <c>202</c> з <c>jobId</c> одразу, а результат стає відомий пізніше.
    /// Сценарій, який читає стан одразу після <c>202</c>, перевіряв би чергу,
    /// а не результат.
    ///
    /// ⛔ <c>jobId</c> ЕКРАНУЄТЬСЯ. Планувальник складає його з імені задачі,
    /// адреси документа й GUID через <c>#</c>
    /// (<c>IRecalculationJob#doc1-p202609#…</c>), а <c>#</c> в URI починає
    /// фрагмент: неекранований ідентифікатор перетворював запит на
    /// <c>/api/v1/jobs/IRecalculationJob</c>, тобто на <c>404</c>. Опитування
    /// при цьому не падало — воно тихо повертало «стану немає», і сценарій
    /// звинувачував перерахунок у тому, що той не завершився. Клієнт
    /// (<c>api/client.ts</c>, <c>JobsPage.tsx</c>) екранує його з першого дня;
    /// не екранував лише цей помічник.
    /// </remarks>
    public static async Task<JsonElement> AwaitJobAsync(
        HttpClient client, string jobId, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonElement last = default;
        var address = new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative);

        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync(address, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                last = JsonDocument.Parse(body).RootElement.Clone();

                var state = last.TryGetProperty("state", out var s) ? s.GetString() : null;
                if (!string.Equals(state, "Queued", StringComparison.Ordinal)
                    && !string.Equals(state, "Running", StringComparison.Ordinal))
                {
                    return last;
                }
            }
            else
            {
                // Кінцевий стан «задачі не існує» теж кінцевий: подальше
                // опитування нічого не дасть.
                return default;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
        }

        return last;
    }

    /// <summary>
    /// Серверні помилки одним рядком — для повідомлення асерту. Без цього
    /// невдалий сценарій показує голий <c>500</c>: <c>ExceptionHandlingMiddleware</c>
    /// навмисно не віддає клієнту текст винятку чи стек (`ФВ-6.11`).
    /// </summary>
    public static string Errors(EcrApiFactory factory) => factory.ErrorsText;
}
