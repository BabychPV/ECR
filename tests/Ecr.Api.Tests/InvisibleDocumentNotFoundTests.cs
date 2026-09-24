// tests/Ecr.Api.Tests/InvisibleDocumentNotFoundTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Документ без гранта для користувача не існує на ЖОДНОМУ маршруті: та сама
/// відповідь, що й на неіснуючий (B-08, UX-прохід, четвертий раунд).
/// </summary>
/// <remarks>
/// ⛔ Що відтворили аналітики: оператор без гранта — <c>GET /documents/9</c> →
/// <c>404</c>, а <c>…/9/tables</c>, <c>…/header</c>, <c>…/validation</c> →
/// <c>403</c> «no access to document 9: NoGrant»; неіснуючий документ — усюди
/// <c>404</c>. Тобто перебором ідентифікаторів видно, які документи існують у
/// чужих проєктах. Тест порівнює відповідь на ЧУЖИЙ документ із відповіддю на
/// НЕІСНУЮЧИЙ — статус, код і речення.
/// </remarks>
[Collection("SqlServer")]
public sealed class InvisibleDocumentNotFoundTests(SqlServerFixture sql)
{
    private const long Missing = 999_999_999L;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B-08")]
    public async Task Чужий_документ_на_кожному_маршруті_читання_виглядає_як_неіснуючий()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        var period = document.PeriodKey.Value;

        using var app = new EcrApiFactory(sql);

        // Функціональні права є, ГРАНТА на проєкт — немає.
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Document.View", "Calculation.View", "Calculation.Recalculate").ConfigureAwait(true);

        foreach (var (method, route, body) in Routes(document.TableInstanceId, period))
        {
            var foreign = await SendAsync(client, method, route(document.DocumentId), body).ConfigureAwait(true);
            var absent = await SendAsync(client, method, route(Missing), body).ConfigureAwait(true);

            Assert.True(
                foreign.Status == HttpStatusCode.NotFound,
                $"{method} {route(document.DocumentId)}: очікували 404, отримали {(int)foreign.Status}\n{foreign.Body}\n{app.ErrorsText}");
            Assert.True(
                absent.Status == HttpStatusCode.NotFound,
                $"{method} {route(Missing)}: {(int)absent.Status}\n{absent.Body}");

            Assert.Equal("ECR-DOC-0404", Code(foreign.Body));
            Assert.Equal($"Document {document.DocumentId} was not found.", Detail(foreign.Body));

            // ⚠ Речення відрізняється від неіснуючого ЛИШЕ ідентифікатором.
            Assert.Equal(
                Detail(absent.Body)!.Replace(Missing.ToString(System.Globalization.CultureInfo.InvariantCulture), "{id}", StringComparison.Ordinal),
                Detail(foreign.Body)!.Replace(document.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture), "{id}", StringComparison.Ordinal));
        }
    }

    /// <summary>Маршрути документа, що читають його зміст або діють від його імені.</summary>
    private static IEnumerable<(string Method, Func<long, string> Route, object? Body)> Routes(long tableInstanceId, int period)
    {
        yield return ("GET", id => $"/api/v1/documents/{id}", null);
        yield return ("GET", id => $"/api/v1/documents/{id}/tables?periodKey={period}", null);
        yield return ("GET", id => $"/api/v1/documents/{id}/tables/status?periodKey={period}", null);
        yield return ("GET", id => $"/api/v1/documents/{id}/header", null);
        yield return ("GET", id => $"/api/v1/documents/{id}/validation?periodKey={period}", null);
        yield return ("GET", id => $"/api/v1/documents/{id}/calculation-results?periodKey={period}", null);
        yield return ("POST", id => $"/api/v1/documents/{id}/validate", new { periodKey = period });
        yield return ("POST", id => $"/api/v1/documents/{id}/recalculate", new { periodKey = period });
        yield return ("GET", id => $"/api/v1/documents/{id}/tables/{tableInstanceId}", null);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient client, string method, string url, object? body)
    {
        var uri = new Uri(url, UriKind.Relative);
        using var response = method == "GET"
            ? await client.GetAsync(uri).ConfigureAwait(false)
            : await client.PostAsJsonAsync(uri, body).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static string? Code(string body) => JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString();

    private static string? Detail(string body) => JsonDocument.Parse(body).RootElement.GetProperty("detail").GetString();
}
