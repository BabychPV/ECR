// tests/Ecr.Api.Tests/Security/DenyReadArchivedAudit.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// НФ-8.9.6 / D-247: межі читання (S6, R-11, D-214) і права журналу змін комірок діють на АРХІВНІ рядки
/// (<c>arc.AuditCellChange</c>) точно так само, як на гарячі.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНІ ДОКАЗИ (detached-worktree):
/// — у <c>AuditReader.BuildCellChangeWhere</c> прибрати умову <c>ColumnDefId IN (… OPENJSON(@visibleColumns))</c>
///   І в <c>GetCellChangesHandler</c> страхувальний відсів <c>readable.CanReadColumn</c> — <c>reader</c> отримує
///   значення прихованих колонок, таблиці й аркуша з архіву: червоніють усі тести цього файлу з <c>reader</c>;
/// — прибрати лише SQL-умову (страхувальний відсів лишити) — червоніє
///   <see cref="Обхід_архіву_курсором_limit_1_дає_рівно_видимі_рядки_без_порожніх_сторінок"/>.
///
/// ⚠ Рядки архіву пишуться прямим <c>INSERT</c> у <c>arc.AuditCellChange</c> із ДАТОЮ 2019-05 і ключами документа
/// цього сценарію: гарячий журнал цих тестів (<c>ArrangeAsync</c>, сьогоднішня дата) лежить поза вікном запиту.
/// </remarks>
public sealed partial class DenyReadTests
{
    private const string ArchiveVisible = "arc-visible-411";
    private const string ArchiveVisibleToo = "arc-visible-412";
    private const string ArchiveDeniedColumn = "arc-secret-column-521";
    private const string ArchiveDeniedTable = "arc-secret-table-631";
    private const string ArchiveDeniedSheet = "arc-secret-sheet-741";

    private static readonly DateTime ArchivedAt = new(2019, 5, 10, 9, 0, 0, DateTimeKind.Utc);

    private static string ArchiveJournal(long documentId, string tail = "")
    {
        var from = new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
        var to = new DateTime(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
        return $"/api/v1/audit/cells?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&documentId={documentId}{tail}";
    }

    /// <summary>Рядок архіву аудиту — прямим ADO: UPDATE/DELETE там заборонені, INSERT — ні.</summary>
    private async Task ArchiveAuditAsync(long documentId, string rowKey, int columnDefId, string newValue, int minute)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO arc.AuditCellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES
                (@changedAt, 201905, @documentId, 1, @rowKey, @columnDefId,
                 N'1', @newValue, 1, N'UserEdit', 0);
            """;

        var moment = command.Parameters.Add("@changedAt", System.Data.SqlDbType.DateTime2);
        moment.Scale = 3;
        moment.Value = ArchivedAt.AddMinutes(minute);
        command.Parameters.AddWithValue("@documentId", documentId);
        command.Parameters.AddWithValue("@rowKey", rowKey);
        command.Parameters.AddWithValue("@columnDefId", columnDefId);
        command.Parameters.AddWithValue("@newValue", newValue);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Видима колонка, заборонена колонка, заборонена таблиця й таблиця забороненого аркуша — усе в архіві.</summary>
    private async Task ArrangeArchiveAsync(Scenario s)
    {
        await ArchiveAuditAsync(s.Doc.DocumentId, s.RowKey, s.Doc.ColumnDefIds[1], ArchiveVisible, 0).ConfigureAwait(false);
        await ArchiveAuditAsync(s.Doc.DocumentId, s.RowKey, s.Doc.ColumnDefIds[2], ArchiveDeniedColumn, 1).ConfigureAwait(false);
        await ArchiveAuditAsync(
            s.Doc.DocumentId, s.DeniedTable.RowKeys[0], s.DeniedTable.ColumnDefIds[0], ArchiveDeniedTable, 2).ConfigureAwait(false);
        await ArchiveAuditAsync(
            s.Doc.DocumentId, s.DeniedSheetTable.RowKeys[0], s.DeniedSheetTable.ColumnDefIds[0], ArchiveDeniedSheet, 3)
            .ConfigureAwait(false);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "НФ-8.9.6")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Архів_змін_не_віддає_значень_прихованих_колонок_таблиць_і_аркушів()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await ArrangeArchiveAsync(s).ConfigureAwait(true);

        var journal = ArchiveJournal(s.Doc.DocumentId);
        var secrets = new[] { ArchiveDeniedColumn, ArchiveDeniedTable, ArchiveDeniedSheet };

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(reader, journal).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var root = JsonDocument.Parse(body).RootElement;
            var items = root.GetProperty("items");

            // Лише видимий архівний рядок — із позначкою «з архіву».
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal(ArchiveVisible, items[0].GetProperty("newValue").GetString());
            Assert.True(items[0].GetProperty("fromArchive").GetBoolean());
            Assert.Equal(1, root.GetProperty("totalCount").GetInt32());

            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(s.DeniedTable.TableCode, body, StringComparison.Ordinal);

            // Підсумок — тими самими межами: приховане в число не потрапляє.
            var from = Uri.EscapeDataString(new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
            var to = Uri.EscapeDataString(new DateTime(2019, 6, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
            var summaryUrl = $"/api/v1/audit/cells/summary?from={from}&to={to}&documentId={s.Doc.DocumentId}";
            var (summaryStatus, summaryBody) = await GetAsync(reader, summaryUrl).ConfigureAwait(true);
            Assert.True(summaryStatus == HttpStatusCode.OK, $"{summaryStatus}: {summaryBody}\n{app.ErrorsText}");
            Assert.Equal(1, JsonDocument.Parse(summaryBody).RootElement.GetProperty("total").GetInt64());

            // Історія однієї прихованої комірки з архіву — порожньо, як у комірки, яку ніхто не правив.
            var cell = ArchiveJournal(
                s.Doc.DocumentId,
                $"&rowKey={Uri.EscapeDataString(s.RowKey)}&columnDefId={s.Doc.ColumnDefIds[2]}");
            var (cellStatus, cellBody) = await GetAsync(reader, cell).ConfigureAwait(true);
            Assert.True(cellStatus == HttpStatusCode.OK, $"{cellStatus}: {cellBody}\n{app.ErrorsText}");
            Assert.Empty(JsonDocument.Parse(cellBody).RootElement.GetProperty("items").EnumerateArray());
            Assert.DoesNotContain(ArchiveDeniedColumn, cellBody, StringComparison.Ordinal);
        }

        // Регресія: без заборон архів віддає всі чотири зміни — і кожна з позначкою.
        using (var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true))
        {
            var (status, body) = await GetAsync(plain, journal).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var root = JsonDocument.Parse(body).RootElement;
            Assert.Equal(4, root.GetProperty("items").GetArrayLength());
            Assert.Equal(4, root.GetProperty("totalCount").GetInt32());
            Assert.All(root.GetProperty("items").EnumerateArray(), i => Assert.True(i.GetProperty("fromArchive").GetBoolean()));

            foreach (var secret in secrets)
            {
                Assert.Contains(secret, body, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "НФ-8.9.6")]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Обхід_архіву_курсором_limit_1_дає_рівно_видимі_рядки_без_порожніх_сторінок()
    {
        // Порядок за ChangedAt: V1, H(колонка), V2, H(таблиця) — приховані рядки між видимими (R-11).
        var s = await ArrangeAsync().ConfigureAwait(true);
        await ArchiveAuditAsync(s.Doc.DocumentId, s.RowKey, s.Doc.ColumnDefIds[1], ArchiveVisible, 0).ConfigureAwait(true);
        await ArchiveAuditAsync(s.Doc.DocumentId, s.RowKey, s.Doc.ColumnDefIds[2], ArchiveDeniedColumn, 1).ConfigureAwait(true);
        await ArchiveAuditAsync(s.Doc.DocumentId, s.RowKey, s.Doc.ColumnDefIds[1], ArchiveVisibleToo, 2).ConfigureAwait(true);
        await ArchiveAuditAsync(
            s.Doc.DocumentId, s.DeniedTable.RowKeys[0], s.DeniedTable.ColumnDefIds[0], ArchiveDeniedTable, 3).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = ArchiveJournal(s.Doc.DocumentId, "&limit=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            var (status, body) = await GetAsync(reader, url).ConfigureAwait(true);
            Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

            var root = JsonDocument.Parse(body).RootElement;
            var items = root.GetProperty("items");
            Assert.True(items.GetArrayLength() == 1, $"сторінка {pages}: очікувався 1 видимий рядок, а не {items.GetArrayLength()}");
            seen.Add(items[0].GetProperty("newValue").GetString()!);

            var next = root.GetProperty("nextCursor");
            cursor = next.ValueKind == JsonValueKind.Null ? null : next.GetString();
            pages++;
            Assert.True(pages <= 3, "курсор не завершується");
        }
        while (cursor is not null);

        Assert.Equal([ArchiveVisible, ArchiveVisibleToo], seen);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "НФ-8.9.6")]
    [Trait("Requirement", "D15-16")]
    public async Task Права_на_архів_ті_самі_що_й_на_гарячий_журнал()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await ArrangeArchiveAsync(s).ConfigureAwait(true);

        // Без жодного права: ні загального журналу, ні історії комірки з архіву.
        var nobody = NewUser("dnn");

        // Лише Document.View + грант на проєкт, БЕЗ Security.ViewAudit: історія ОДНІЄЇ комірки — так,
        // загальний журнал (і архівний теж) — 403.
        var viewer = NewUser("dnv");
        var viewerRole = NewRole("DNV_V");

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            db.Users.AddRange(nobody, viewer);
            db.Roles.Add(viewerRole);
            await db.SaveChangesAsync().ConfigureAwait(true);

            db.RolePermissions.Add(new RolePermission(viewerRole.Id, "Document.View"));
            db.ResourceGrants.Add(new ResourceGrant(viewerRole.Id, ResourceKind.Project, s.Doc.ProjectId, GrantLevel.Read));
            db.RoleAssignments.Add(new RoleAssignment(viewerRole.Id, viewer.Id, null));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var general = ArchiveJournal(s.Doc.DocumentId);
        var oneCell = ArchiveJournal(
            s.Doc.DocumentId, $"&rowKey={Uri.EscapeDataString(s.RowKey)}&columnDefId={s.Doc.ColumnDefIds[1]}");

        using var app = new EcrApiFactory(sql);

        using (var client = await SignedInAsync(app, nobody.UserName).ConfigureAwait(true))
        {
            foreach (var url in new[] { general, oneCell })
            {
                var (status, body) = await GetAsync(client, url).ConfigureAwait(true);
                Assert.Equal(HttpStatusCode.Forbidden, status);
                Assert.DoesNotContain(ArchiveVisible, body, StringComparison.Ordinal);
            }
        }

        using (var client = await SignedInAsync(app, viewer.UserName).ConfigureAwait(true))
        {
            var (generalStatus, generalBody) = await GetAsync(client, general).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, generalStatus);
            Assert.DoesNotContain(ArchiveVisible, generalBody, StringComparison.Ordinal);

            var (cellStatus, cellBody) = await GetAsync(client, oneCell).ConfigureAwait(true);
            Assert.True(cellStatus == HttpStatusCode.OK, $"{cellStatus}: {cellBody}\n{app.ErrorsText}");

            var items = JsonDocument.Parse(cellBody).RootElement.GetProperty("items");
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal(ArchiveVisible, items[0].GetProperty("newValue").GetString());
            Assert.True(items[0].GetProperty("fromArchive").GetBoolean());
        }
    }
}
