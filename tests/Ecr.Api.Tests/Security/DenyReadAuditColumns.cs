// tests/Ecr.Api.Tests/Security/DenyReadAuditColumns.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// ✎ audit-filters-contract AC-8: <c>GET /audit/cells/columns</c> — колонки документа, змінені у вікні (фільтр «Column»),
/// за тими самими межами читання, що й журнал.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>GetCellChangesHandler.ChangedColumnsAsync</c> не застосувати межі читання
/// (<c>WithVisibleLineageAsync</c> і другий рубіж) — <c>reader</c> отримає три колонки замість однієї, разом із кодом
/// забороненої таблиці, і тест червоніє.
/// </remarks>
public sealed partial class DenyReadTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Перелік_змінених_колонок_журналу_не_містить_прихованих_колонок_і_таблиць()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var from = DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        var url = $"/api/v1/audit/cells/columns?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}"
                  + $"&documentId={s.Doc.DocumentId}";

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(reader, url).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var only = Assert.Single(JsonDocument.Parse(body).RootElement.EnumerateArray());
            Assert.Equal(s.Doc.ColumnDefIds[1], only.GetProperty("columnDefId").GetInt32());
            Assert.Equal(s.ColumnCodes[1], only.GetProperty("columnCode").GetString());
            Assert.Equal(s.Doc.SheetCode, only.GetProperty("sheetCode").GetString());
            Assert.DoesNotContain(s.ColumnCodes[2], body, StringComparison.Ordinal);
            Assert.DoesNotContain(s.DeniedTable.TableCode, body, StringComparison.Ordinal);
        }

        // Регресія: без заборон — усі три змінені колонки, кожна один раз.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(plain, url).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var ids = JsonDocument.Parse(body).RootElement.EnumerateArray()
                .Select(c => c.GetProperty("columnDefId").GetInt32())
                .Order()
                .ToList();
            Assert.Equal(
                new[] { s.Doc.ColumnDefIds[1], s.Doc.ColumnDefIds[2], s.DeniedTable.ColumnDefIds[0] }.Order(),
                ids);
        }

        // Вікно поза змінами — порожньо; вікно понад стелю — 422, як у журналі.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var past = DateTime.UtcNow.AddDays(-40);
            var (status, body) = await GetAsync(
                plain,
                $"/api/v1/audit/cells/columns?from={Uri.EscapeDataString(past.ToString("O", CultureInfo.InvariantCulture))}"
                + $"&to={Uri.EscapeDataString(past.AddDays(1).ToString("O", CultureInfo.InvariantCulture))}"
                + $"&documentId={s.Doc.DocumentId}").ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}");
            Assert.Equal(0, JsonDocument.Parse(body).RootElement.GetArrayLength());

            var year = DateTime.UtcNow.AddDays(-365).ToString("O", CultureInfo.InvariantCulture);
            (status, _) = await GetAsync(
                plain,
                $"/api/v1/audit/cells/columns?from={Uri.EscapeDataString(year)}&to={Uri.EscapeDataString(to)}"
                + $"&documentId={s.Doc.DocumentId}").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        }
    }
}
