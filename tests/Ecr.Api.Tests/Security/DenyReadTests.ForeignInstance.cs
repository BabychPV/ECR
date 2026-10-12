// tests/Ecr.Api.Tests/Security/DenyReadTests.ForeignInstance.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

public sealed partial class DenyReadTests
{
    /// <summary>
    /// P3 (оракул існування): екземпляр таблиці, що існує в ІНШОМУ документі, на <c>GET …/tables/{id}</c> віддає
    /// ту саму відмову, що й неіснуючий і схований (D-6): <c>404 ECR-DOC-0404 tableInstance</c>, ті самі
    /// <c>title</c>/<c>detail</c>/набір полів — з точністю до ідентифікатора.
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>GetTableSliceHandler</c> повернути кидок
    /// <c>err.ECR-DOC-0404.tableInstanceNotInDocument</c> замість <c>DocumentVisibility.TableInstanceNotFound</c> —
    /// червоніє (messageKey і detail відрізняються від неіснуючого).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Екземпляр_чужого_документа_на_зрізі_нерозрізний_від_неіснуючого_і_схованого()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var other = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(true);
        Assert.NotEqual(s.Doc.DocumentId, other.DocumentId);

        using var app = new EcrApiFactory(sql);

        const long missing = 999_999_998L;
        foreach (var user in new[] { s.Reader, s.Plain })
        {
            using var client = await SignedInAsync(app, user).ConfigureAwait(true);

            var (absentStatus, absent) = await GetAsync(client, Slice(s, missing)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, absentStatus);

            var (foreignStatus, foreign) = await GetAsync(client, Slice(s, other.TableInstanceId)).ConfigureAwait(true);
            Assert.True(foreignStatus == HttpStatusCode.NotFound, $"{foreignStatus}: {foreign}\n{app.ErrorsText}");

            Assert.Equal(
                Shape(absent, missing),
                Shape(foreign, other.TableInstanceId));

            Assert.Equal("err.ECR-DOC-0404.tableInstance", Prop(foreign, "messageKey"));
            Assert.DoesNotContain("tableInstanceNotInDocument", foreign, StringComparison.Ordinal);
        }

        // Схований від читача екземпляр свого документа — те саме тіло (D-6 не зачеплено).
        using var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true);
        var (_, absentForReader) = await GetAsync(reader, Slice(s, missing)).ConfigureAwait(true);
        var (hiddenStatus, hidden) = await GetAsync(reader, Slice(s, s.DeniedTable.TableInstanceId)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, hiddenStatus);
        Assert.Equal(Shape(absentForReader, missing), Shape(hidden, s.DeniedTable.TableInstanceId));
    }

    private static string Prop(string body, string name)
    {
        var json = JsonDocument.Parse(body).RootElement;
        return json.TryGetProperty(name, out var v) ? v.ToString() : string.Empty;
    }

    /// <summary>
    /// Те, що бачить клієнт: статус, код, ключ, заголовок, подробиця й ПЕРЕЛІК полів тіла — з ідентифікатором,
    /// заміненим на <c>{id}</c>. <c>traceId</c> і подібне порівнянню не підлягає.
    /// </summary>
    private static string Shape(string body, long id)
    {
        var json = JsonDocument.Parse(body).RootElement;
        var fields = string.Join(",", json.EnumerateObject().Select(p => p.Name).Where(n => n != "traceId").Order(StringComparer.Ordinal));
        var idText = id.ToString(CultureInfo.InvariantCulture);
        string P(string n) => Prop(body, n).Replace(idText, "{id}", StringComparison.Ordinal);

        return string.Join(
            "|", fields, P("status"), P("errorCode"), P("messageKey"), P("title"), P("detail"), P("tableInstanceId"));
    }
}
