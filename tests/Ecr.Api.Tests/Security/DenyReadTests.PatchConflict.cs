// tests/Ecr.Api.Tests/Security/DenyReadTests.PatchConflict.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S6: відповідь <c>PATCH …/cells</c> не називає значення колонки, якої
/// користувач не бачить.
/// </summary>
/// <remarks>
/// ⛔ До фіксу перевірка версії (<c>ECR-CELL-0409</c>) ішла ДО перевірки прав і
/// дочитувала в перелік розбіжностей чинне значення кожної комірки батчу,
/// автора й момент. Тож <c>PATCH</c> із навмисно застарілим <c>baseVersion</c>
/// і кодом забороненої колонки читав її значення — навіть у користувача, що
/// має лише <c>Read</c> і взагалі нічого не може записати.
/// </remarks>
public sealed partial class DenyReadTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task PATCH_із_застарілою_версією_не_віддає_значення_прихованої_колонки()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var (status, body) = await StalePatchAsync(reader, s).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.Conflict, $"{status}: {body}\n{app.ErrorsText}");

            // Конфлікт на місці — обидві комірки, але чуже значення лише видимої.
            var conflicts = JsonDocument.Parse(body).RootElement.GetProperty("conflicts").EnumerateArray().ToList();
            Assert.Equal(2, conflicts.Count);
            Assert.Contains(Digits(Visible), body, StringComparison.Ordinal);
            Assert.DoesNotContain(Digits(DeniedColumn), body, StringComparison.Ordinal);

            var hidden = Assert.Single(conflicts, c => c.GetProperty("columnCode").GetString() == s.ColumnCodes[2]);
            Assert.Equal(JsonValueKind.Null, hidden.GetProperty("theirValue").ValueKind);
            Assert.Equal(JsonValueKind.Null, hidden.GetProperty("theirChangedAt").ValueKind);
        }

        // Регресія: без заборон конфлікт називає обидва чужі значення.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var (status, body) = await StalePatchAsync(plain, s).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.Conflict, $"{status}: {body}\n{app.ErrorsText}");
            Assert.Contains(Digits(Visible), body, StringComparison.Ordinal);
            Assert.Contains(Digits(DeniedColumn), body, StringComparison.Ordinal);
        }
    }

    /// <summary><c>PATCH</c> рядка з версією, якої немає, — видима C2 і заборонена C3.</summary>
    private static async Task<(HttpStatusCode Status, string Body)> StalePatchAsync(HttpClient client, Scenario s)
    {
        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Doc.TableInstanceId,
                periodKey = s.Doc.PeriodKey.Value,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = s.RowKey,
                        baseVersion = "0x0000000000000001",
                        cells = new[]
                        {
                            new { columnCode = s.ColumnCodes[1], value = (object)"1" },
                            new { columnCode = s.ColumnCodes[2], value = (object)"2" },
                        },
                    },
                },
            }).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }
}
