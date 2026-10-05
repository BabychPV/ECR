// tests/Ecr.Api.Tests/Security/DenyReadTests.VersionList.cs
using System.Net;
using System.Text.Json;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Workflow;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// L1-18 (AN-31): перелік версій документа не називає версій (аркуш, автор, час) аркуша,
/// закритого користувачу забороною.
/// </summary>
/// <remarks>
/// ⛔ До фіксу <c>GET /documents/{id}/versions</c> віддавав КОЖНУ версію документа за період,
/// знаючи лише про видимість проєкту: роль із <c>Deny</c> на аркуш бачила код аркуша,
/// автора й момент подання аркуша, якого не бачить. МУТАЦІЙНИЙ ДОКАЗ: прибрати фільтр
/// <c>CanReadSheet</c> у <c>ListDocumentVersionsHandler</c> — червоніє рядок <c>reader</c>.
/// </remarks>
public sealed partial class DenyReadTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    [Trait("Requirement", "ФВ-5.22")]
    public async Task Перелік_версій_не_містить_версій_забороненого_аркуша()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var visibleVersion = await SnapshotSheetAsync(s, s.Doc.SheetDefId).ConfigureAwait(true);
        var deniedVersion = await SnapshotSheetAsync(s, s.DeniedSheetTable.SheetDefId).ConfigureAwait(true);
        var url = $"/api/v1/documents/{s.Doc.DocumentId}/versions?periodKey={s.Doc.PeriodKey.Value}";

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(reader, url).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var ids = JsonDocument.Parse(body).RootElement.EnumerateArray()
                .Select(v => v.GetProperty("versionId").GetInt64()).ToList();
            Assert.Equal([visibleVersion], ids);
            Assert.DoesNotContain(deniedVersion, ids);
        }

        // Регресія: без заборон видно обидві версії.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(plain, url).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var ids = JsonDocument.Parse(body).RootElement.EnumerateArray()
                .Select(v => v.GetProperty("versionId").GetInt64()).Order().ToList();
            Assert.Equal([visibleVersion, deniedVersion], ids);
        }
    }

    /// <summary>Порожній зріз подання аркуша за період сценарію — лише для переліку версій.</summary>
    private async Task<long> SnapshotSheetAsync(Scenario s, int sheetDefId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        var snapshot = new SubmissionSnapshot(
            s.Doc.DocumentId, sheetDefId, s.Doc.PeriodKey.Value, s.Doc.TemplateVersionId,
            "[]", null, null, SubmissionPayload.Write([]), new byte[32], DateTime.UtcNow, s.ReaderId);
        db.SubmissionSnapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return snapshot.Id;
    }
}
