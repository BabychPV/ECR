using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Обмеження таблиць Permits (PRM01_Permits) на **реальному** SQL Server: CHECK, UNIQUE і
/// відсутність каскаду. Доменні тести їх не бачать — вони не торкаються бази.
/// </summary>
/// <remarks>
/// Кожен випадок виконується в транзакції, яка відкочується. Зовнішні ключі на батьківські
/// таблиці (RegistryDef, TemplateVersion, …) у цій транзакції вимкнено — тут перевіряються
/// саме обмеження Permits, а не наповнення довідників. Єдиний FK, що лишається ввімкненим, —
/// <c>FK_PermitBindCol_Binding</c>: його поведінку при видаленні перевіряє окремий тест.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitConstraintsTests(SqlServerFixture sql)
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..16];

    private static string Type(string code, int cat = 0, int status = 0, string? at = null, string? by = null) =>
        $"INSERT cfg.PermitTypeDef(Code,NameL10n,Category,HeaderRegistryDefId,Status,PublishedAt,PublishedByUserId) " +
        $"VALUES(N'{code}',N'{{}}',{cat},1,{status},{at ?? "NULL"},{by ?? "NULL"});";

    private static string Table(int typeId, string code, int registryId) =>
        $"INSERT cfg.PermitTypeTable(PermitTypeDefId,Code,NameL10n,Ordinal,RegistryDefId) " +
        $"VALUES({typeId},N'{code}',N'{{}}',1,{registryId});";

    private static string Field(int fieldId, string cols, string vals) =>
        $"INSERT cfg.PermitFieldMeta(RegistryFieldDefId,PermitTypeDefId,{cols}) VALUES({fieldId},1,{vals});";

    private static string Binding(string code, int tv = 1, int sheet = 1, int mode = 0, int rule = 0, string? header = null) =>
        $"INSERT cfg.PermitSheetBinding(Code,TemplateVersionId,SheetTableDefId,PermitTypeDefId,PermitTypeTableId,Mode,SelectionRule,HeaderFieldDefId) " +
        $"VALUES(N'{code}',{tv},{sheet},1,1,{mode},{rule},{header ?? "NULL"});";

    private static string BindCol(int bindingId, string code, int role = 0, int col = 1) =>
        $"INSERT cfg.PermitSheetBindingColumn(BindingId,PermitFieldCode,Role,SheetColumnDefId,Ordinal) " +
        $"VALUES({bindingId},N'{code}',{role},{col},1);";

    private const string Now = "SYSUTCDATETIME()";

    /// <summary>Виконує пакет у відкатній транзакції; повертає помилку SQL Server або null.</summary>
    private async Task<SqlException?> RunAsync(string batch)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        try
        {
            // Вимикаємо всі FK п'яти таблиць, крім зв'язку колонок із прив'язкою.
            await ExecAsync(connection, tx, """
                DECLARE @s nvarchar(max) = N'';
                SELECT @s += N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name)
                           + N' NOCHECK CONSTRAINT ' + QUOTENAME(fk.name) + N';'
                FROM sys.foreign_keys fk
                JOIN sys.tables t ON t.object_id = fk.parent_object_id
                WHERE SCHEMA_NAME(t.schema_id) = N'cfg'
                  AND t.name IN (N'PermitTypeDef', N'PermitTypeTable', N'PermitFieldMeta',
                                 N'PermitSheetBinding', N'PermitSheetBindingColumn')
                  AND fk.name <> N'FK_PermitBindCol_Binding';
                EXEC sp_executesql @s;
                """).ConfigureAwait(false);
            await ExecAsync(connection, tx, batch).ConfigureAwait(false);
            return null;
        }
        catch (SqlException ex)
        {
            return ex;
        }
        // Відкат — у DisposeAsync транзакції (`await using`), Commit не викликається.
    }

    private static async Task ExecAsync(SqlConnection c, SqlTransaction tx, string text)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = text;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task Violates(string constraint, string batch)
    {
        var ex = await RunAsync(batch);
        Assert.NotNull(ex);
        Assert.Contains(constraint, ex!.Message, StringComparison.Ordinal);
    }

    private async Task Valid(string batch)
    {
        var ex = await RunAsync(batch);
        Assert.True(ex is null, ex?.Message);
    }

    // ---------- PermitTypeDef ----------

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Опублікований_тип_вимагає_PublishedAt_і_PublishedBy()
    {
        var c = Uid();
        await Violates("CK_PermitType_Published", Type(c, status: 1));
        await Violates("CK_PermitType_Published", Type(c, status: 1, at: Now));
        await Violates("CK_PermitType_Published", Type(c, status: 1, by: "1"));
        await Valid(Type(c, status: 1, at: Now, by: "1"));
        await Valid(Type(c, status: 0));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Категорія_і_статус_типу_мають_межі()
    {
        await Violates("CK_PermitType_Category", Type(Uid(), cat: 4));
        await Violates("CK_PermitType_Status", Type(Uid(), status: 2));
        await Valid(Type(Uid(), cat: 3));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Код_типу_унікальний()
    {
        var c = Uid();
        await Violates("UQ_PermitTypeDef", Type(c) + Type(c));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Код_таблиці_унікальний_у_межах_типу_і_реєстр_таблиці_один()
    {
        var c = Uid();
        var t = Type(c) + "DECLARE @t int = SCOPE_IDENTITY();";
        await Violates("UQ_PermitTypeTable",
            t + "INSERT cfg.PermitTypeTable(PermitTypeDefId,Code,NameL10n,Ordinal,RegistryDefId) VALUES(@t,N'a',N'{}',1,1);" +
                "INSERT cfg.PermitTypeTable(PermitTypeDefId,Code,NameL10n,Ordinal,RegistryDefId) VALUES(@t,N'a',N'{}',2,2);");
        await Violates("UQ_PermitTypeTable_Registry",
            t + "INSERT cfg.PermitTypeTable(PermitTypeDefId,Code,NameL10n,Ordinal,RegistryDefId) VALUES(@t,N'a',N'{}',1,1);" +
                "INSERT cfg.PermitTypeTable(PermitTypeDefId,Code,NameL10n,Ordinal,RegistryDefId) VALUES(@t,N'b',N'{}',2,1);");
        await Valid(
            t + "INSERT cfg.PermitTypeTable(PermitTypeDefId,Code,NameL10n,Ordinal,RegistryDefId) VALUES(@t,N'a',N'{}',1,1);" +
                "INSERT cfg.PermitTypeTable(PermitTypeDefId,Code,NameL10n,Ordinal,RegistryDefId) VALUES(@t,N'b',N'{}',2,2);");
    }

    // ---------- PermitFieldMeta ----------

    [Theory]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "NULL,NULL,NULL", true)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "0,5,NULL", true)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "1,5,NULL", true)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "2,NULL,N'tbl'", true)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "NULL,5,NULL", false)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "NULL,NULL,N'tbl'", false)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "0,NULL,NULL", false)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "0,5,N'tbl'", false)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "2,5,N'tbl'", false)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "2,NULL,NULL", false)]
    [InlineData("ListScope,ListRegistryDefId,ListSiblingTableCode", "2,5,NULL", false)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_поля_має_рівно_три_допустимі_форми(string cols, string vals, bool ok)
    {
        var batch = Type(Uid()) + Field(1, cols, vals);
        if (ok)
        {
            await Valid(batch);
        }
        else
        {
            await Violates("CK_PermitField_List", batch);
        }
    }

    [Theory]
    [InlineData("10,2", true)]
    [InlineData("38,38", true)]
    [InlineData("1,NULL", true)]
    [InlineData("NULL,NULL", true)]
    [InlineData("0,NULL", false)]
    [InlineData("39,NULL", false)]
    [InlineData("NULL,2", false)]
    [InlineData("5,6", false)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Точність_і_масштаб_поля_мають_межі(string pair, bool ok)
    {
        var batch = Type(Uid()) + Field(1, "Precision,Scale", pair);
        if (ok)
        {
            await Valid(batch);
        }
        else
        {
            await Violates("CK_PermitField_Num", batch);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Область_переліку_поза_межами_відхиляється()
    {
        var ex = await RunAsync(Type(Uid()) + Field(1, "ListScope,ListRegistryDefId", "3,5"));
        Assert.NotNull(ex);
        Assert.Contains("CK_PermitField_", ex!.Message, StringComparison.Ordinal);
    }

    // ---------- PermitSheetBinding / Column ----------

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Режим_і_правило_прив_язки_мають_межі()
    {
        await Violates("CK_PermitBinding_Mode", Binding(Uid(), mode: 2));
        await Violates("CK_PermitBinding_Rule", Binding(Uid(), rule: 2));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поле_шапки_обовязкове_рівно_для_FromHeaderLookup()
    {
        await Violates("CK_PermitBinding_Header", Binding(Uid(), rule: 1));
        await Violates("CK_PermitBinding_Header", Binding(Uid(), rule: 0, header: "7"));
        await Valid(Binding(Uid(), rule: 1, header: "7"));
        await Valid(Binding(Uid(), rule: 0));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прив_язка_унікальна_за_кодом_і_за_версією_шаблону_з_аркушем()
    {
        await Violates("UQ_PermitSheetBinding_Code", Binding("dup") + Binding("dup", sheet: 2));
        await Violates("UQ_PermitSheetBinding_Sheet", Binding(Uid()) + Binding(Uid()));
        await Valid(Binding(Uid()) + Binding(Uid(), sheet: 2) + Binding(Uid(), tv: 2));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Роль_колонки_прив_язки_має_межі_і_колонка_аркуша_одна_на_прив_язку()
    {
        var b = Binding(Uid()) + "DECLARE @b int = SCOPE_IDENTITY();";
        await Violates("CK_PermitBindCol_Role", b + BindCol(0, "x", role: 3).Replace("VALUES(0", "VALUES(@b"));
        await Violates("UQ_PermitBindCol_Column",
            b + BindCol(0, "x").Replace("VALUES(0", "VALUES(@b") + BindCol(0, "y").Replace("VALUES(0", "VALUES(@b"));
        await Valid(b + BindCol(0, "x", role: 2).Replace("VALUES(0", "VALUES(@b"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_прив_язки_з_колонками_відхиляється_без_каскаду()
    {
        var setup = Binding(Uid()) + "DECLARE @b int = SCOPE_IDENTITY();" +
                    BindCol(0, "x").Replace("VALUES(0", "VALUES(@b");
        await Violates("FK_PermitBindCol_Binding", setup + "DELETE cfg.PermitSheetBinding WHERE Id = @b;");
        // Явне видалення колонок перед прив'язкою — штатний шлях — проходить.
        await Valid(setup + "DELETE cfg.PermitSheetBindingColumn WHERE BindingId = @b;" +
                    "DELETE cfg.PermitSheetBinding WHERE Id = @b;");
    }
}
