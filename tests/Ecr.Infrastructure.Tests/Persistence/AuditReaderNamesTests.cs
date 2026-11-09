using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>R-18</c>/<c>X-35</c>: журнал змін віддає ІМЕНА, а не лише номери.
/// </summary>
/// <remarks>
/// ⛔ Екран журналу показував «By user 3 · Document 1 · R1 · 2»: автора, документ
/// і колонку — внутрішніми ідентифікаторами, з якими аудитор нічого не
/// зробить. Імена приєднуються в самому запиті (після вибору сторінки), тому
/// перевіряються проти справжньої СУБД — підміна довела б лише те, що їй
/// сказали повернути.
/// </remarks>
[Collection("SqlServer")]
public sealed class AuditReaderNamesTests(SqlServerFixture sql)
{
    private const string DisplayName = "D. Nurlanova";

    private static readonly DateTime At = new(2026, 3, 4, 9, 15, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Журнал_комірок_називає_автора_документ_і_колонку()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var userId = await CreateUserAsync("dnurlanova", DisplayName);
        var (businessKey, columnCode, dataType) = await FactsAsync(doc);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync([Change(doc, userId)], CancellationToken.None);

        var page = await new AuditReader(db).ReadCellChangesAsync(
            new CellChangeFilter(At.AddMinutes(-1), At.AddMinutes(1), DocumentId: doc.DocumentId),
            new CursorRequest(50),
            CancellationToken.None);

        var row = Assert.Single(page.Items);

        // ⛔ Мутація «прибрати LEFT JOIN sec.[User]» (стара форма запиту) дає null.
        Assert.Equal(DisplayName, row.ChangedByDisplayName);
        Assert.Equal(businessKey, row.DocumentBusinessKey);
        Assert.Equal(columnCode, row.ColumnCode);
        Assert.Equal(dataType.ToString(), row.ColumnDataType);
        Assert.NotNull(row.ColumnHeaderL10n);

        // Ідентифікатори нікуди не зникли — фільтри журналу на них і стоять.
        Assert.Equal(userId, row.ChangedByUserId);
        Assert.Equal(doc.ColumnDefIds[1], row.ColumnDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Автор_якого_вже_немає_не_ховає_рядка_журналу()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync([Change(doc, 2_000_000_002)], CancellationToken.None);

        var page = await new AuditReader(db).ReadCellChangesAsync(
            new CellChangeFilter(At.AddMinutes(-1), At.AddMinutes(1), DocumentId: doc.DocumentId),
            new CursorRequest(50),
            CancellationToken.None);

        // ⛔ LEFT, не INNER: мутація на INNER JOIN прибирає рядок зовсім.
        var row = Assert.Single(page.Items);
        Assert.Null(row.ChangedByDisplayName);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Журнал_структури_називає_автора()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var userId = await CreateUserAsync("dnurlanova-s", DisplayName);
        var entityType = $"cfg.R18-{Guid.NewGuid():N}"[..40];

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteStructureChangeAsync(
            new StructureChangeRecord(
                At, doc.TemplateVersionId, entityType, 7, ChangeClass.Safe, "SaveRules",
                null, "{}", null, userId, null),
            CancellationToken.None);

        var reader = new AuditReader(db);

        var journal = await reader.ReadStructureJournalAsync(
            new StructureChangeFilter(At.AddMinutes(-1), At.AddMinutes(1), EntityType: entityType),
            new CursorRequest(50),
            CancellationToken.None);

        Assert.Equal(DisplayName, Assert.Single(journal.Items).ChangedByDisplayName);

        var history = await reader.ReadStructureChangesAsync([entityType], 7, 10, CancellationToken.None);

        Assert.Equal(DisplayName, Assert.Single(history).ChangedByDisplayName);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Журнал_комірок_називає_аркуш_таблицю_і_рядок_без_додаткових_запитів()
    {
        // UI-38, C1: «Документ · Таблиця › Рядок» без розшифровки ідентифікаторів на клієнті.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var userId = await CreateUserAsync("dnurlanova-c1", DisplayName);
        var (sheetCode, tableCode, rowKey, rowLabelJson) = await StructureFactsAsync(doc);

        await using var db = builder.CreateContext();
        await new AuditWriter(db).WriteCellChangesAsync([Change(doc, userId) with { RowKey = rowKey }], CancellationToken.None);

        var page = await new AuditReader(db).ReadCellChangesAsync(
            new CellChangeFilter(At.AddMinutes(-1), At.AddMinutes(1), DocumentId: doc.DocumentId),
            new CursorRequest(50),
            CancellationToken.None);

        var row = Assert.Single(page.Items);
        Assert.Equal(sheetCode, row.SheetCode);
        Assert.Equal(tableCode, row.TableCode);
        Assert.NotNull(row.SheetNameL10n);
        Assert.NotNull(row.TableNameL10n);
        Assert.Equal(LocalizedText.FromJson(rowLabelJson).Values, row.RowLabelL10n!.Values);
    }

    /// <summary>
    /// F13 c1 (09.11): рядок видалення проєкту називає КОД проєкту зі знімка (<c>OldJson.code</c>), а не номер
    /// (чинного рядка <c>doc.Project</c> уже немає); чинний проєкт — свій чинний код.
    /// </summary>
    /// <remarks>Мутація: прибрати <c>SnapshotCode</c> у <c>AuditReader</c> — підпис видаленого null, червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "R-18")]
    public async Task Журнал_структури_називає_код_видаленого_і_чинного_проєкту()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);
        var at = new DateTime(2026, 3, 4, 9, 17, 31, DateTimeKind.Utc);
        var deletedId = 2_000_000_000 + Random.Shared.Next(1, 100_000_000);

        await using var db = builder.CreateContext();
        var writer = new AuditWriter(db);
        await writer.WriteStructureChangeAsync(
            new StructureChangeRecord(
                at, doc.TemplateVersionId, "Project", deletedId, ChangeClass.Guarded, "Delete",
                """{"code":"Pavlo_TEST","status":"Draft"}""", null, null, 1, null),
            CancellationToken.None);
        await writer.WriteStructureChangeAsync(
            new StructureChangeRecord(
                at, doc.TemplateVersionId, "Project", doc.ProjectId, ChangeClass.Guarded, "Activate",
                null, "{}", null, 1, null),
            CancellationToken.None);

        var journal = await new AuditReader(db).ReadStructureJournalAsync(
            new StructureChangeFilter(at.AddMinutes(-1), at.AddMinutes(1), EntityType: "Project"),
            new CursorRequest(500),
            CancellationToken.None);

        Assert.Equal("Pavlo_TEST", journal.Items.Single(i => i.EntityId == deletedId).EntityLabel);
        Assert.Equal(await ProjectCodeAsync(doc.ProjectId), journal.Items.Single(i => i.EntityId == doc.ProjectId).EntityLabel);
    }

    private async Task<string> ProjectCodeAsync(int projectId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Code FROM doc.Project WHERE Id = @id;";
        command.Parameters.AddWithValue("@id", projectId);

        return (string)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    private async Task<(string SheetCode, string TableCode, string RowKey, string RowLabelJson)> StructureFactsAsync(TestDocument doc)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) sd.Code, td.Code, r.RowKey, r.LabelL10n
              FROM cfg.ColumnDef AS c
              JOIN cfg.TableDef AS td ON td.Id = c.TableDefId
              JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
              JOIN cfg.RowDef AS r ON r.TableDefId = td.Id AND r.IsDeleted = 0
             WHERE c.Id = @col
             ORDER BY r.Ordinal;
            """;
        command.Parameters.AddWithValue("@col", doc.ColumnDefIds[1]);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private static CellChangeRecord Change(TestDocument doc, int userId)
        => new(
            At,
            new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[1]),
            doc.DocumentId,
            "R1",
            OldValue: "53.1771000000000000",
            NewValue: "54.2000000000000000",
            ChangedByUserId: userId,
            Origin: "UserEdit",
            IsLateEdit: false,
            CorrelationId: null);

    /// <summary>Бізнес-ключ документа, код і тип колонки — те, що має назвати журнал.</summary>
    private async Task<(string BusinessKey, string ColumnCode, CellDataType DataType)> FactsAsync(TestDocument doc)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.BusinessKey, c.Code, c.DataType
              FROM doc.Document AS d CROSS JOIN cfg.ColumnDef AS c
             WHERE d.Id = @doc AND c.Id = @col;
            """;
        command.Parameters.AddWithValue("@doc", doc.DocumentId);
        command.Parameters.AddWithValue("@col", doc.ColumnDefIds[1]);

        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));

        return (reader.GetString(0), reader.GetString(1), (CellDataType)reader.GetByte(2));
    }

    /// <summary>Користувач напряму в <c>sec.User</c> (провайдер Windows — без пароля).</summary>
    private async Task<int> CreateUserAsync(string userName, string displayName)
    {
        var unique = Guid.NewGuid().ToString("N");

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sec.[User]
                (UserName, DisplayName, Provider, WindowsSid, SecurityStamp, CreatedAt)
            OUTPUT INSERTED.Id
            VALUES (@userName, @displayName, 0, @sid, @stamp, SYSUTCDATETIME());
            """;
        command.Parameters.AddWithValue("@userName", $"{userName}-{unique}");
        command.Parameters.AddWithValue("@displayName", displayName);
        command.Parameters.AddWithValue("@sid", $"S-1-5-21-{unique}");
        command.Parameters.AddWithValue("@stamp", unique);

        return (int)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }
}
