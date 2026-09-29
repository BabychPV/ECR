// tests/Ecr.Api.Tests/Security/DenyReadTests.ExportAudit.cs
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S20: замовлення експорту й віддане завантаження — події журналу безпеки;
/// межі S6 у події — лише КІЛЬКІСТЬ прихованого, без назв.
/// </summary>
/// <remarks>
/// ⚠ У цьому ж класі, бо сценарій <c>reader</c> уже має заборони на колонку,
/// таблицю й аркуш — тобто непорожні межі S6, які й має порахувати подія.
/// </remarks>
public sealed partial class DenyReadTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S20")]
    public async Task S20_експорт_і_завантаження_пишуть_події_з_кількістю_прихованого_без_назв()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true);
        using var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true);

        var exportId = await OrderExportAsync(app, reader, s).ConfigureAwait(true);

        var requested = Assert.Single(await EventsAsync("DocumentExportRequested", s.ReaderId).ConfigureAwait(true));
        using (var json = JsonDocument.Parse(requested))
        {
            var root = json.RootElement;
            Assert.Equal(s.Doc.DocumentId, root.GetProperty("documentId").GetInt64());
            Assert.Equal("xlsx", root.GetProperty("format").GetString());
            Assert.True(root.GetProperty("hiddenTableCount").GetInt32() > 0, requested);
            Assert.True(root.GetProperty("hiddenColumnCount").GetInt32() > 0, requested);
        }

        // ⛔ Назв прихованого в журналі немає — лише кількість.
        Assert.DoesNotContain(s.ColumnCodes[2], requested, StringComparison.Ordinal);
        Assert.DoesNotContain("hiddenTableDefIds", requested, StringComparison.OrdinalIgnoreCase);

        // Чуже завантаження нічого не віддає — і нічого не пише.
        var (foreign, _) = await GetAsync(plain, $"/api/v1/documents/{s.Doc.DocumentId}/export/{exportId}")
            .ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreign);

        var (own, ownBody) = await GetAsync(reader, $"/api/v1/documents/{s.Doc.DocumentId}/export/{exportId}")
            .ConfigureAwait(true);
        Assert.True(own == HttpStatusCode.OK, $"{own}: {ownBody}\n{app.ErrorsText}");

        var downloaded = Assert.Single(await EventsAsync("DocumentExportDownloaded", s.ReaderId).ConfigureAwait(true));
        using (var json = JsonDocument.Parse(downloaded))
        {
            Assert.Equal(s.Doc.DocumentId, json.RootElement.GetProperty("documentId").GetInt64());
            Assert.Equal("xlsx", json.RootElement.GetProperty("format").GetString());
            Assert.True(json.RootElement.GetProperty("bytes").GetInt32() > 0, downloaded);
        }

        Assert.Empty(await EventsAsync("DocumentExportDownloaded", PlainIdOf(s)).ConfigureAwait(true));
    }

    /// <summary>Id користувача <c>plain</c> — сценарій тримає лише ім'я.</summary>
    private int PlainIdOf(Scenario s)
    {
        using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return db.Users.Where(u => u.UserName == s.Plain).Select(u => u.Id).Single();
    }

    /// <summary><c>DetailsJson</c> подій заданого типу від заданого користувача.</summary>
    private async Task<List<string>> EventsAsync(string eventType, int byUserId)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ISNULL(DetailsJson, N'') FROM aud.SecurityEvent
            WHERE EventType = @e AND ChangedByUserId = @u
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("@e", eventType);
        command.Parameters.AddWithValue("@u", byUserId);

        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
