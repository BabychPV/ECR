// tests/Ecr.Api.Tests/Security/DenyReadAuditPagination.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>R-11: межі читання застосовуються в SQL ДО сторінки, тож курсор не видає існування прихованих змін.</summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати присвоєння <c>VisibleColumnIds</c> у <c>GetCellChangesHandler.HandleAsync</c>
/// (повернути відсів лише після читання сторінки) — обидва тести червоніють: сторінка <c>limit=1</c> порожня
/// або з <c>nextCursor</c> за самими прихованими змінами.
/// </remarks>
public sealed partial class DenyReadTests
{
    private static string AuditUrl(long documentId, string tail = "")
    {
        var from = DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        return $"/api/v1/audit/cells?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&documentId={documentId}{tail}";
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Обхід_журналу_курсором_limit_1_дає_рівно_видимі_рядки_без_порожніх_сторінок()
    {
        // Порядок за Id: V1, H2, H3 (з Arrange), потім ще V4 — приховані рядки між видимими.
        var s = await ArrangeAsync().ConfigureAwait(true);
        await AuditAsync(s.Doc.DocumentId, s.RowKey, s.Doc.ColumnDefIds[1], "777777.25").ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = AuditUrl(s.Doc.DocumentId, "&limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            var (status, body) = await GetAsync(reader, url).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var root = JsonDocument.Parse(body).RootElement;
            var items = root.GetProperty("items");
            Assert.True(items.GetArrayLength() == 1, $"сторінка {pages}: очікувався 1 видимий рядок, а не {items.GetArrayLength()}");
            seen.Add(items[0].GetProperty("newValue").GetString()!);

            if (pages == 0)
            {
                Assert.Equal(2, root.GetProperty("totalCount").GetInt32());
            }

            var next = root.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
            pages++;
            Assert.True(pages <= 3, "курсор не завершується");
        }
        while (cursor is not null);

        Assert.Equal([AuditVisible, "777777.25"], seen);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Сторінка_лише_з_прихованих_змін_порожня_і_без_nextCursor()
    {
        // Два рядки забороненої таблиці збігаються з q; читач бачить нуль, тож «є ще» бути не може.
        var s = await ArrangeAsync().ConfigureAwait(true);
        await AuditAsync(s.Doc.DocumentId, s.DeniedTable.RowKeys[0], s.DeniedTable.ColumnDefIds[0], "888888.25").ConfigureAwait(true);

        var tail = "&limit=1&q=" + Uri.EscapeDataString(s.DeniedTable.RowKeys[0]);

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(reader, AuditUrl(s.Doc.DocumentId, tail)).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var root = JsonDocument.Parse(body).RootElement;
            Assert.Equal(0, root.GetProperty("items").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("nextCursor").ValueKind);
            Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
        }

        // Контроль: без заборон та сама сторінка має продовження.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(plain, AuditUrl(s.Doc.DocumentId, tail)).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var root = JsonDocument.Parse(body).RootElement;
            Assert.Equal(1, root.GetProperty("items").GetArrayLength());
            Assert.Equal(JsonValueKind.String, root.GetProperty("nextCursor").ValueKind);
        }
    }
}
