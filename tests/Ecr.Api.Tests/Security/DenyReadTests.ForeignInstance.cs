// tests/Ecr.Api.Tests/Security/DenyReadTests.ForeignInstance.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

public sealed partial class DenyReadTests
{
    /// <summary>
    /// P3-7 (оракул існування екземпляра таблиці): ВІДСУТНІЙ екземпляр, екземпляр ЧУЖОГО документа, екземпляр
    /// НЕВИДИМОГО документа й неіснуючий документ дають на <c>GET …/tables/{id}</c>, <c>PATCH …/cells</c> і
    /// <c>POST …/rows</c> ОДНЕ тіло <c>404 ECR-DOC-0404 err.ECR-DOC-0404.document</c> (status/errorCode/messageKey/
    /// title/detail/набір полів) — з точністю до ідентифікатора документа з маршруту.
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>GetTableSliceHandler</c> повернути кидок <c>tableInstanceNotInDocument</c> (або
    /// <c>tableInstance</c>) замість <c>DocumentVisibility.ResolveInDocumentAsync</c> — червоніють рядки GET.
    /// Схована таблиця ВИДИМОГО документа лишається <c>tableInstance</c> (D-6, прийнято) — це інший тест.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Відсутній_чужий_і_недоступний_екземпляр_дають_одне_тіло_на_читанні_і_записі()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var other = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(true);
        Assert.NotEqual(s.Doc.DocumentId, other.DocumentId);

        using var app = new EcrApiFactory(sql);

        const long missingInstance = 999_999_998L;
        const long missingDocument = 999_999_997L;

        // (документ з маршруту, екземпляр): відсутній; чужого документа; документ без гранта користувача; без документа.
        var cases = new (string Name, long Document, long Instance)[]
        {
            ("відсутній", s.Doc.DocumentId, missingInstance),
            ("чужий", s.Doc.DocumentId, other.TableInstanceId),
            ("недоступний документ", other.DocumentId, other.TableInstanceId),
            ("неіснуючий документ", missingDocument, missingInstance),
        };

        foreach (var user in new[] { s.Reader, s.Plain })
        {
            using var client = await SignedInAsync(app, user).ConfigureAwait(true);

            var shapes = new List<(string Label, string Shape)>();
            foreach (var (name, document, instance) in cases)
            {
                var (getStatus, getBody) = await GetAsync(
                    client, $"/api/v1/documents/{document}/tables/{instance}").ConfigureAwait(true);
                shapes.Add(($"GET/{name}", Check(getStatus, getBody, document, instance, app)));

                var (patchStatus, patchBody) = await SendAsync(client, HttpMethod.Patch, $"/api/v1/documents/{document}/cells", new
                {
                    tableInstanceId = instance,
                    periodKey = s.Doc.PeriodKey.Value,
                    origin = "UserEdit",
                    rows = new[]
                    {
                        new
                        {
                            rowKey = "INVX",
                            baseVersion = (string?)null,
                            cells = new object[] { new { columnCode = s.ColumnCodes[0], value = (object)1m } },
                        },
                    },
                }).ConfigureAwait(true);
                shapes.Add(($"PATCH/{name}", Check(patchStatus, patchBody, document, instance, app)));

                var (postStatus, postBody) = await SendAsync(
                    client, HttpMethod.Post, $"/api/v1/documents/{document}/rows", new { tableInstanceId = instance, rowKey = "INVX" })
                    .ConfigureAwait(true);
                shapes.Add(($"POST/{name}", Check(postStatus, postBody, document, instance, app)));
            }

            var expected = shapes[0].Shape;
            foreach (var (label, shape) in shapes)
            {
                Assert.True(expected == shape, $"{user}: {label}: {shape}\nочікувалось: {expected}");
            }
        }
    }

    private static string Check(HttpStatusCode status, string body, long document, long instance, EcrApiFactory app)
    {
        Assert.True(status == HttpStatusCode.NotFound, $"{status}: {body}\n{app.ErrorsText}");
        Assert.Equal("err.ECR-DOC-0404.document", Prop(body, "messageKey"));
        Assert.DoesNotContain("tableInstance", body, StringComparison.OrdinalIgnoreCase);

        return Shape(body, document, instance);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpClient client, HttpMethod method, string url, object content)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(content),
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static string Prop(string body, string name)
    {
        var json = JsonDocument.Parse(body).RootElement;
        return json.TryGetProperty(name, out var v) ? v.ToString() : string.Empty;
    }

    /// <summary>
    /// Те, що бачить клієнт: статус, код, ключ, заголовок, подробиця, <c>documentId</c> і ПЕРЕЛІК полів тіла —
    /// з ідентифікаторами, замінюваними на <c>{doc}</c>/<c>{id}</c>. <c>traceId</c>/<c>correlationId</c> не порівнюються.
    /// </summary>
    private static string Shape(string body, long document, long instance)
    {
        var json = JsonDocument.Parse(body).RootElement;
        var fields = string.Join(
            ",",
            json.EnumerateObject().Select(p => p.Name)
                .Where(n => n is not ("traceId" or "correlationId")).Order(StringComparer.Ordinal));

        string P(string n) => Prop(body, n)
            .Replace(document.ToString(CultureInfo.InvariantCulture), "{doc}", StringComparison.Ordinal)
            .Replace(instance.ToString(CultureInfo.InvariantCulture), "{id}", StringComparison.Ordinal);

        return string.Join("|", fields, P("status"), P("errorCode"), P("messageKey"), P("title"), P("detail"), P("documentId"));
    }
}
