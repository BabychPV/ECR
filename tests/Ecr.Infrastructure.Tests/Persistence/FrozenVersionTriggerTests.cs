using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D5: тригери незмінності структури (<c>10-triggers.sql</c>) тримають
/// <b>обидва</b> замороженіх стани версії — <c>Published</c> і
/// <c>Deprecated</c> — і всі чотири способи зламати структуру: <c>UPDATE</c>,
/// <c>INSERT</c> нового дочірнього рядка, <c>DELETE</c> і перенесення рядка між
/// таблицями (<c>TableDefId</c>).
/// </summary>
/// <remarks>
/// ⚠ Усе робиться прямим SQL повз застосунок: C5 закрив гонку публікації лише
/// у застосунку, а тригер має спрацювати на будь-якому шляху до бази.
///
/// ⚠ Ланцюг завжди будується в <c>Draft</c> (там усе можна), дочірні рядки для
/// перевірок додаються ДО заморожування, і лише тоді версія переходить у
/// <c>Published</c>/<c>Deprecated</c>. Видалення й перенесення перевіряються на
/// СВІЖИХ рядках без залежностей: вбудований <c>FK</c> (наприклад із
/// <c>doc.CellValue</c>) відмовив би раніше за тригер, і тест довів би не те.
/// </remarks>
[Collection("SqlServer")]
public sealed class FrozenVersionTriggerTests(SqlServerFixture sql)
{
    private const int Published = 1;
    private const int Deprecated = 2;

    private const int ColumnStructure = 50001;
    private const int RowStructure = 50002;
    private const int FormulaChange = 50003;
    private const int InsertIntoFrozen = 50004;
    private const int DeleteFromFrozen = 50005;

    private const string FrozenKey = "structurallyFrozen";

    // ── Deprecated: те, що тригер раніше пропускав ───────────────────────────

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Структурна_зміна_колонки_виведеної_з_обігу_версії_відхиляється()
    {
        var chain = await ChainAsync(Deprecated);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET DataType = 3 WHERE Id = {chain.Doc.ColumnDefIds[0]}"));

        Assert.Equal(ColumnStructure, error.Number);
        Assert.Contains("CloneFrom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Зміна_RowKey_виведеної_з_обігу_версії_відхиляється()
    {
        var chain = await ChainAsync(Deprecated);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.RowDef SET RowKey = N'ЗМІНЕНО' WHERE Id = {chain.Doc.RowDefIds[0]}"));

        Assert.Equal(RowStructure, error.Number);
    }

    [Theory]
    [InlineData("UPDATE cfg.FormulaDef SET Expression = N'2+2' WHERE Id = {0}")]
    [InlineData("DELETE FROM cfg.FormulaDef WHERE Id = {0}")]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Зміна_або_видалення_формули_виведеної_з_обігу_версії_відхиляється(string statement)
    {
        var chain = await ChainAsync(Deprecated);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, statement, chain.FormulaId)));

        Assert.Equal(FormulaChange, error.Number);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Презентаційні_правки_виведеної_з_обігу_версії_дозволені()
    {
        var chain = await ChainAsync(Deprecated);

        // ⚠ Той самий білий список, що в `TemplateVersionStore.ApplyPresentationAsync`
        // (`PatchPresentationHandler` стан версії не перевіряє): відкат версії
        // не має забороняти виправлену одруку в заголовку.
        await ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET Ordinal = 77, IsHidden = 1, HeaderL10n = N'{{\"en\":\"Виправлено\"}}' WHERE Id = {chain.Doc.ColumnDefIds[0]}");
        await ExecuteAsync(
            $"UPDATE cfg.RowDef SET LabelL10n = N'{{\"en\":\"Виправлено\"}}' WHERE Id = {chain.Doc.RowDefIds[0]}");

        Assert.Equal(77, await ScalarAsync<int>(
            $"SELECT Ordinal FROM cfg.ColumnDef WHERE Id = {chain.Doc.ColumnDefIds[0]}"));
    }

    // ── INSERT ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Published)]
    [InlineData(Deprecated)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Нова_колонка_в_замороженій_версії_відхиляється(int status)
    {
        var chain = await ChainAsync(status);

        var error = await Assert.ThrowsAsync<SqlException>(
            () => InsertColumnAsync(chain.Doc.TableDefId, "LATE"));

        Assert.Equal(InsertIntoFrozen, error.Number);
        Assert.Contains(FrozenKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Published)]
    [InlineData(Deprecated)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Новий_рядок_у_замороженій_версії_відхиляється(int status)
    {
        var chain = await ChainAsync(status);

        var error = await Assert.ThrowsAsync<SqlException>(
            () => InsertRowAsync(chain.Doc.TableDefId, "LATE"));

        Assert.Equal(InsertIntoFrozen, error.Number);
        Assert.Contains(FrozenKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Published)]
    [InlineData(Deprecated)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Нова_формула_в_замороженій_версії_відхиляється(int status)
    {
        var chain = await ChainAsync(status);

        var error = await Assert.ThrowsAsync<SqlException>(
            () => InsertFormulaAsync(chain.Doc.TableDefId, chain.Doc.ColumnDefIds[2]));

        Assert.Equal(InsertIntoFrozen, error.Number);
        Assert.Contains(FrozenKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Вставка_однією_командою_у_чернетку_і_в_заморожену_відхиляється_цілком()
    {
        var frozen = await ChainAsync(Published);
        var draft = await ChainAsync(status: null);
        var tag = Guid.NewGuid().ToString("N")[..8];

        // ⚠ Тригер працює над УСІМ набором `inserted`, а не над першим рядком:
        // один рядок у замороженій таблиці валить команду цілком, і рядок у
        // чернетці теж не лишається.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($$"""
            INSERT INTO cfg.ColumnDef (TableDefId, Code, HeaderL10n, Ordinal, DataType)
            VALUES ({{draft.Doc.TableDefId}}, N'MIX_D_{{tag}}', N'{"en":"x"}', 91, 2),
                   ({{frozen.Doc.TableDefId}}, N'MIX_F_{{tag}}', N'{"en":"x"}', 92, 2);
            """));

        Assert.Equal(InsertIntoFrozen, error.Number);
        Assert.Equal(0, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM cfg.ColumnDef WHERE Code = N'MIX_D_{tag}'"));
    }

    // ── DELETE ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Published)]
    [InlineData(Deprecated)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Видалення_колонки_замороженої_версії_відхиляється(int status)
    {
        var chain = await ChainAsync(status);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"DELETE FROM cfg.ColumnDef WHERE Id = {chain.ExtraColumnId}"));

        Assert.Equal(DeleteFromFrozen, error.Number);
        Assert.Contains(FrozenKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM cfg.ColumnDef WHERE Id = {chain.ExtraColumnId}"));
    }

    [Theory]
    [InlineData(Published)]
    [InlineData(Deprecated)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Видалення_рядка_замороженої_версії_відхиляється(int status)
    {
        var chain = await ChainAsync(status);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"DELETE FROM cfg.RowDef WHERE Id = {chain.ExtraRowId}"));

        Assert.Equal(DeleteFromFrozen, error.Number);
        Assert.Contains(FrozenKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(1, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM cfg.RowDef WHERE Id = {chain.ExtraRowId}"));
    }

    // ── Перенесення між таблицями (TableDefId) ───────────────────────────────

    [Theory]
    [InlineData(Published)]
    [InlineData(Deprecated)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Колонку_замороженої_версії_не_можна_перенести_в_таблицю_чернетки(int status)
    {
        var frozen = await ChainAsync(status);
        var draft = await ChainAsync(status: null);

        // ⚠ Тригер раніше дивився лише на НОВУ таблицю (`inserted.TableDefId`):
        // перенесення ІЗ замороженої в чернетку виглядало правкою чернетки.
        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET TableDefId = {draft.Doc.TableDefId} WHERE Id = {frozen.ExtraColumnId}"));

        Assert.Equal(ColumnStructure, error.Number);
        Assert.Equal(frozen.Doc.TableDefId, await ScalarAsync<int>(
            $"SELECT TableDefId FROM cfg.ColumnDef WHERE Id = {frozen.ExtraColumnId}"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Колонку_чернетки_не_можна_перенести_в_таблицю_замороженої_версії()
    {
        var frozen = await ChainAsync(Published);
        var draft = await ChainAsync(status: null);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET TableDefId = {frozen.Doc.TableDefId} WHERE Id = {draft.ExtraColumnId}"));

        Assert.Equal(ColumnStructure, error.Number);
    }

    [Theory]
    [InlineData(Published)]
    [InlineData(Deprecated)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Рядок_замороженої_версії_не_можна_перенести_в_таблицю_чернетки(int status)
    {
        var frozen = await ChainAsync(status);
        var draft = await ChainAsync(status: null);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.RowDef SET TableDefId = {draft.Doc.TableDefId} WHERE Id = {frozen.ExtraRowId}"));

        Assert.Equal(RowStructure, error.Number);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Рядок_чернетки_не_можна_перенести_в_таблицю_замороженої_версії()
    {
        var frozen = await ChainAsync(Published);
        var draft = await ChainAsync(status: null);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.RowDef SET TableDefId = {frozen.Doc.TableDefId} WHERE Id = {draft.ExtraRowId}"));

        Assert.Equal(RowStructure, error.Number);
    }

    [Theory]
    [InlineData(Published)]
    [InlineData(Deprecated)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Формулу_не_можна_перенести_в_заморожену_таблицю_і_з_неї(int status)
    {
        var frozen = await ChainAsync(status);
        var draft = await ChainAsync(status: null);

        // З замороженої — у чернетку: раніше ловилося (перевірялася стара таблиця).
        var outOfFrozen = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.FormulaDef SET TableDefId = {draft.Doc.TableDefId} WHERE Id = {frozen.FormulaId}"));
        Assert.Equal(FormulaChange, outOfFrozen.Number);

        // З чернетки — у заморожену: раніше проходило (стара таблиця — чернетка).
        var intoFrozen = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.FormulaDef SET TableDefId = {frozen.Doc.TableDefId} WHERE Id = {draft.FormulaId}"));
        Assert.Equal(FormulaChange, intoFrozen.Number);
    }

    // ── Легальні шляхи: тригер не заважає ────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Чернетка_редагується_повністю_вставка_правка_видалення_перенесення()
    {
        var one = await ChainAsync(status: null);
        var two = await ChainAsync(status: null);

        var columnId = await InsertColumnAsync(one.Doc.TableDefId, "EDIT");
        var rowId = await InsertRowAsync(one.Doc.TableDefId, "EDIT");
        var formulaId = await InsertFormulaAsync(one.Doc.TableDefId, one.Doc.ColumnDefIds[2]);

        await ExecuteAsync($"UPDATE cfg.ColumnDef SET DataType = 3 WHERE Id = {columnId}");
        await ExecuteAsync($"UPDATE cfg.RowDef SET RowKey = N'EDITED_{Guid.NewGuid():N}' WHERE Id = {rowId}");
        await ExecuteAsync($"UPDATE cfg.FormulaDef SET Expression = N'2+2' WHERE Id = {formulaId}");

        // Між таблицями ЧЕРНЕТОК (навіть різних версій) — легально: структура ще
        // не зафіксована; заборона діє від першого `Published`.
        await ExecuteAsync($"UPDATE cfg.ColumnDef SET TableDefId = {two.Doc.TableDefId} WHERE Id = {columnId}");
        await ExecuteAsync($"UPDATE cfg.RowDef SET TableDefId = {two.Doc.TableDefId} WHERE Id = {rowId}");
        await ExecuteAsync($"UPDATE cfg.FormulaDef SET TableDefId = {two.Doc.TableDefId} WHERE Id = {formulaId}");

        await ExecuteAsync($"DELETE FROM cfg.FormulaDef WHERE Id = {formulaId}");
        await ExecuteAsync($"DELETE FROM cfg.RowDef WHERE Id = {rowId}");
        await ExecuteAsync($"DELETE FROM cfg.ColumnDef WHERE Id = {columnId}");

        Assert.Equal(0, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM cfg.ColumnDef WHERE Id = {columnId}"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Публікація_потім_виведення_з_обігу_проходять_із_дочірніми_рядками()
    {
        var chain = await ChainAsync(status: null);
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var now = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);

        // Доменні переходи, як їх робить `PublishTemplateVersionHandler`: цей
        // UPDATE зачіпає лише `cfg.TemplateVersion`, а формули й колонки
        // (у тому числі з іншим `EvaluationOrder`) від тригера не страждають.
        await using (var db = builder.CreateContext())
        {
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == chain.Doc.TemplateVersionId);
            var formula = await db.FormulaDefs.SingleAsync(f => f.Id == chain.FormulaId);
            formula.SetEvaluationOrder(3);
            await db.SaveChangesAsync();

            version.Publish(1, now);
            await db.SaveChangesAsync();
        }

        await using (var db = builder.CreateContext())
        {
            var version = await db.TemplateVersions.SingleAsync(v => v.Id == chain.Doc.TemplateVersionId);
            version.Deprecate(1, now);
            await db.SaveChangesAsync();
        }

        Assert.Equal(Deprecated, await ScalarAsync<byte>(
            $"SELECT Status FROM cfg.TemplateVersion WHERE Id = {chain.Doc.TemplateVersionId}"));

        // І далі презентація працює (див. `Презентаційні_правки_…`).
        await ExecuteAsync($"UPDATE cfg.ColumnDef SET Ordinal = 88 WHERE Id = {chain.Doc.ColumnDefIds[0]}");
    }

    // ── Побудова ланцюга ─────────────────────────────────────────────────────

    /// <summary>
    /// Будує ланцюг у <c>Draft</c>, додає «свіжу» колонку, рядок і формулу
    /// (без залежностей) і лише потім переводить версію в <paramref name="status"/>
    /// (<c>null</c> — лишає чернеткою).
    /// </summary>
    private async Task<Chain> ChainAsync(int? status)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        var columnId = await InsertColumnAsync(doc.TableDefId, "X");
        var rowId = await InsertRowAsync(doc.TableDefId, "X");
        var formulaId = await InsertFormulaAsync(doc.TableDefId, doc.ColumnDefIds[1]);

        if (status is Published or Deprecated)
        {
            await ExecuteAsync(
                "UPDATE cfg.TemplateVersion SET Status = " + Published +
                ", PublishedAt = SYSUTCDATETIME(), PublishedByUserId = 1 " +
                $"WHERE Id = {doc.TemplateVersionId}");
        }

        if (status == Deprecated)
        {
            await ExecuteAsync(
                "UPDATE cfg.TemplateVersion SET Status = " + Deprecated +
                ", DeprecatedAt = SYSUTCDATETIME(), DeprecatedByUserId = 1 " +
                $"WHERE Id = {doc.TemplateVersionId}");
        }

        return new Chain(doc, columnId, rowId, formulaId);
    }

    private sealed record Chain(TestDocument Doc, int ExtraColumnId, int ExtraRowId, int FormulaId);

    private Task<int> InsertColumnAsync(int tableDefId, string tag)
        => ScalarAsync<int>($$"""
            INSERT INTO cfg.ColumnDef (TableDefId, Code, HeaderL10n, Ordinal, DataType)
            VALUES ({{tableDefId}}, N'{{tag}}_{{Guid.NewGuid():N}}', N'{"en":"extra"}', 90, 2);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);

    private Task<int> InsertRowAsync(int tableDefId, string tag)
        => ScalarAsync<int>($$"""
            INSERT INTO cfg.RowDef (TableDefId, RowKey, Ordinal, LabelL10n, RowKind)
            VALUES ({{tableDefId}}, N'{{tag}}_{{Guid.NewGuid():N}}', 90, N'{"en":"extra"}', 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);

    private Task<int> InsertFormulaAsync(int tableDefId, int columnDefId)
        // Scope = 0 (Column) вимагає ColumnDefId — це стежить CK_Formula_Scope.
        => ScalarAsync<int>($"""
            INSERT INTO cfg.FormulaDef (TableDefId, ColumnDefId, Scope, Dialect, Expression)
            VALUES ({tableDefId}, {columnDefId}, 0, 0, N'1+1');
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return (T)value!;
    }
}
