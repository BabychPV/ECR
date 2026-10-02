// tests/Ecr.Api.Tests/RegistryDefinitionHttpExtensions.cs
using System.Net.Http.Json;
using System.Text.Json;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>PUT /api/v1/registries/{code}/definition</c> вимагає <c>If-Match</c> із <c>definitionVersion</c>
/// (ФВ-8.12, борг рев'ю): тести, яким версія не предмет перевірки, читають її прямо перед записом —
/// так само, як це робить екран конструктора.
/// </summary>
internal static class RegistryDefinitionHttpExtensions
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Як <c>PutAsJsonAsync</c>, але з <c>If-Match</c> — актуальною <c>definitionVersion</c> опису.
    /// </summary>
    /// <param name="client">Клієнт із правами на опис і публікацію.</param>
    /// <param name="url">Адреса <c>…/registries/{code}/definition</c>.</param>
    /// <param name="body">Повний стан опису.</param>
    /// <returns>Відповідь сервера на <c>PUT</c>.</returns>
    public static async Task<HttpResponseMessage> PutDefinitionAsync(this HttpClient client, Uri url, object body)
    {
        ArgumentNullException.ThrowIfNull(client);
        var version = await ReadVersionAsync(client, url).ConfigureAwait(false);
        return await PutDefinitionAsync(client, url, body, version).ConfigureAwait(false);
    }

    /// <summary>Те саме з явною версією в <c>If-Match</c> (<c>null</c> — без заголовка).</summary>
    /// <param name="client">Клієнт із правами на опис і публікацію.</param>
    /// <param name="url">Адреса <c>…/registries/{code}/definition</c>.</param>
    /// <param name="body">Повний стан опису.</param>
    /// <param name="ifMatch">Значення заголовка як є, або <c>null</c>.</param>
    /// <returns>Відповідь сервера на <c>PUT</c>.</returns>
    public static async Task<HttpResponseMessage> PutDefinitionAsync(
        this HttpClient client, Uri url, object body, string? ifMatch)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body, mediaType: null, Web),
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    /// <summary>Поточна <c>definitionVersion</c> опису у вигляді значення <c>If-Match</c>.</summary>
    /// <param name="client">Клієнт із правом читання.</param>
    /// <param name="url">Адреса <c>…/registries/{code}/definition</c>.</param>
    /// <returns>Версія в лапках, як її віддає <c>ETag</c>-споживач.</returns>
    public static async Task<string> ReadVersionAsync(HttpClient client, Uri url)
    {
        ArgumentNullException.ThrowIfNull(client);
        var response = await client.GetAsync(url).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var version = JsonDocument.Parse(text).RootElement.GetProperty("definitionVersion").GetInt32();
        return $"\"{version}\"";
    }
}
