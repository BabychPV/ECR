// tests/Ecr.Api.Tests/Security/DenyReadAuditTotalCount.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>UI-38, C4 / R-11: <c>totalCount</c> журналу змін лічить лише те, що читач бачить.</summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>GetCellChangesHandler.HandleAsync</c> замінити фільтр лічильників
/// <c>readable.CanReadColumn</c> на «усе» — <c>reader</c> отримає 3 замість 1 і тест червоніє.
/// </remarks>
public sealed partial class DenyReadTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Підсумок_журналу_не_рахує_змін_прихованих_колонок_і_таблиць()
    {
        // UI-38, C2 / R-11: GET /audit/cells/summary -- ті самі межі читання, що й сторінка.
        var s = await ArrangeAsync().ConfigureAwait(true);
        var from = DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        var url = $"/api/v1/audit/cells/summary?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&documentId={s.Doc.DocumentId}";

        using var app = new EcrApiFactory(sql);

        foreach (var (user, expected) in new[] { (s.Reader, 1), (s.Plain, 3) })
        {
            using var client = await SignedInAsync(app, user).ConfigureAwait(true);
            var (status, body) = await GetAsync(client, url).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var root = JsonDocument.Parse(body).RootElement;
            Assert.Equal(expected, root.GetProperty("total").GetInt64());
            Assert.False(root.TryGetProperty("late", out _), "late не віддається без виміру/індексу");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Пошук_q_не_знаходить_рядків_забороненої_таблиці_ні_в_сторінці_ні_в_числі()
    {
        // UI-38, C3: збіг за ключем рядка забороненої таблиці не повинен бути оракулом існування.
        var s = await ArrangeAsync().ConfigureAwait(true);
        var from = DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        var url = $"/api/v1/audit/cells?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}"
                  + $"&documentId={s.Doc.DocumentId}&q={Uri.EscapeDataString(s.DeniedTable.RowKeys[0])}";

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(reader, url).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var root = JsonDocument.Parse(body).RootElement;
            Assert.Equal(0, root.GetProperty("items").GetArrayLength());
            Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
            Assert.DoesNotContain(AuditDeniedTable, body, StringComparison.Ordinal);
        }

        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(plain, url).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
            Assert.Contains(AuditDeniedTable, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task TotalCount_журналу_не_рахує_змін_прихованих_колонок_і_таблиць()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var from = DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        var journal = $"/api/v1/audit/cells?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&documentId={s.Doc.DocumentId}";

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(reader, journal).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var root = JsonDocument.Parse(body).RootElement;
            Assert.Equal(1, root.GetProperty("items").GetArrayLength());

            // Три зміни в журналі, дві — прихованих: число дорівнює видимому.
            Assert.Equal(1, root.GetProperty("totalCount").GetInt32());

            // C1: імена структури лише видимого рядка; заборонена таблиця (код) у відповіді відсутня.
            var item = root.GetProperty("items")[0];
            Assert.Equal(JsonValueKind.String, item.GetProperty("sheetCode").ValueKind);
            Assert.Equal(JsonValueKind.String, item.GetProperty("tableCode").ValueKind);
            Assert.DoesNotContain(s.DeniedTable.TableCode, body, StringComparison.Ordinal);
        }

        // Регресія: без заборон бачимо всі три.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(plain, journal).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
            Assert.Equal(3, JsonDocument.Parse(body).RootElement.GetProperty("totalCount").GetInt32());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "UI-38")]
    public async Task TotalCount_журналу_лише_на_першій_сторінці_а_за_курсором_null()
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: рахувати `totalCount` і за курсором (без `firstPage` у `GetCellChangesHandler`) -- червоніє.
        var s = await ArrangeAsync().ConfigureAwait(true);
        var from = DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture);
        var to = DateTime.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        var journal = $"/api/v1/audit/cells?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}"
                      + $"&documentId={s.Doc.DocumentId}&limit=1";

        using var app = new EcrApiFactory(sql);
        using var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true);

        var (status, body) = await GetAsync(plain, journal).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        var first = JsonDocument.Parse(body).RootElement;
        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        (status, body) = await GetAsync(plain, journal + "&cursor=" + Uri.EscapeDataString(cursor!)).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");
        var next = JsonDocument.Parse(body).RootElement;
        Assert.Equal(JsonValueKind.Null, next.GetProperty("totalCount").ValueKind);
        Assert.Equal(1, next.GetProperty("items").GetArrayLength());
    }
}
