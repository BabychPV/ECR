using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Зовнішні ключі таблиць Permits існують, увімкнені й без каскаду. <see cref="PermitConstraintsTests"/>
/// вимикає 14 із 15 FK у своїй транзакції, тож самі ключі там не перевіряються.
/// </summary>
[Collection("SqlServer")]
public sealed class PermitForeignKeysTests(SqlServerFixture sql)
{
    // Очікуване — з PermitConfiguration.cs (HasConstraintName): ім'я FK -> (таблиця-власник, посилана таблиця).
    private static readonly (string Fk, string Owner, string Referenced)[] Expected =
    [
        ("FK_PermitType_HeaderReg", "cfg.PermitTypeDef", "cfg.RegistryDef"),
        ("FK_PermitType_PublishedBy", "cfg.PermitTypeDef", "sec.User"),
        ("FK_PermitType_UpdatedBy", "cfg.PermitTypeDef", "sec.User"),
        ("FK_PermitTypeTable_Type", "cfg.PermitTypeTable", "cfg.PermitTypeDef"),
        ("FK_PermitTypeTable_Reg", "cfg.PermitTypeTable", "cfg.RegistryDef"),
        ("FK_PermitField_Field", "cfg.PermitFieldMeta", "cfg.RegistryFieldDef"),
        ("FK_PermitField_Type", "cfg.PermitFieldMeta", "cfg.PermitTypeDef"),
        ("FK_PermitField_ListReg", "cfg.PermitFieldMeta", "cfg.RegistryDef"),
        ("FK_PermitBinding_TV", "cfg.PermitSheetBinding", "cfg.TemplateVersion"),
        ("FK_PermitBinding_Sheet", "cfg.PermitSheetBinding", "cfg.TableDef"),
        ("FK_PermitBinding_Type", "cfg.PermitSheetBinding", "cfg.PermitTypeDef"),
        ("FK_PermitBinding_Table", "cfg.PermitSheetBinding", "cfg.PermitTypeTable"),
        ("FK_PermitBinding_Header", "cfg.PermitSheetBinding", "cfg.HeaderFieldDef"),
        ("FK_PermitBindCol_Binding", "cfg.PermitSheetBindingColumn", "cfg.PermitSheetBinding"),
        ("FK_PermitBindCol_Column", "cfg.PermitSheetBindingColumn", "cfg.ColumnDef"),
    ];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Усі_FK_Permits_існують_увімкнені_і_без_каскаду()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT fk.name,
                   SCHEMA_NAME(po.schema_id) + N'.' + po.name,
                   SCHEMA_NAME(ro.schema_id) + N'.' + ro.name,
                   fk.is_disabled, fk.delete_referential_action
            FROM sys.foreign_keys fk
            JOIN sys.tables po ON po.object_id = fk.parent_object_id
            JOIN sys.tables ro ON ro.object_id = fk.referenced_object_id
            WHERE SCHEMA_NAME(po.schema_id) = N'cfg'
              AND po.name IN (N'PermitTypeDef', N'PermitTypeTable', N'PermitFieldMeta',
                              N'PermitSheetBinding', N'PermitSheetBindingColumn');
            """;
        var actual = new List<(string Fk, string Owner, string Referenced)>();
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var name = r.GetString(0);
                actual.Add((name, r.GetString(1), r.GetString(2)));
                Assert.False(r.GetBoolean(3), $"{name} вимкнено (is_disabled=1)");
                Assert.True(r.GetByte(4) == 0, $"{name}: delete_referential_action={r.GetByte(4)}, очікувано 0 (NO ACTION)");
            }
        }

        Assert.Equal(
            Expected.OrderBy(e => e.Fk, StringComparer.Ordinal).Select(e => $"{e.Fk}: {e.Owner} -> {e.Referenced}"),
            actual.OrderBy(e => e.Fk, StringComparer.Ordinal).Select(e => $"{e.Fk}: {e.Owner} -> {e.Referenced}"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тип_таблиці_з_неіснуючим_типом_відхиляється_увімкненим_FK()
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync();
        // Вимикаємо лише FK на реєстр; FK_PermitTypeTable_Type лишається ввімкненим.
        await ExecAsync(connection, tx, "ALTER TABLE cfg.PermitTypeTable NOCHECK CONSTRAINT FK_PermitTypeTable_Reg;");
        var ex = await Assert.ThrowsAsync<SqlException>(() => ExecAsync(connection, tx,
            "INSERT cfg.PermitTypeTable(PermitTypeDefId,Code,NameL10n,Ordinal,RegistryDefId) VALUES(-1,N'x',N'{}',1,1);"));
        Assert.Equal(547, ex.Number);
        Assert.Contains("FK_PermitTypeTable_Type", ex.Message, StringComparison.Ordinal);
        // Відкат — у DisposeAsync транзакції (`await using`), Commit не викликається.
    }

    private static async Task ExecAsync(SqlConnection c, SqlTransaction tx, string text)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = text;
        await cmd.ExecuteNonQueryAsync();
    }
}
