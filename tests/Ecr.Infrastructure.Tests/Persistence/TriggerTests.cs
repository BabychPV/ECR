using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Незмінність опублікованої версії забезпечується **на рівні БД**, а не лише
/// кодом: тригер має спрацювати навіть на прямому <c>UPDATE</c> повз застосунок.
/// </summary>
/// <remarks>
/// Окремо перевіряється сумісність із EF Core: без
/// <c>.ToTable(t =&gt; t.HasTrigger(...))</c> <c>SaveChanges</c> падає в
/// рантаймі через <c>OUTPUT</c>-клаузу (ТЗ §13.5 п.1) — помилку легко
/// пропустити до першого запису в проді.
/// </remarks>
[Collection("SqlServer")]
public sealed class TriggerTests(SqlServerFixture sql)
{
    /// <summary>Опублікована версія: <c>TemplateVersionStatus.Published</c> = 1.</summary>
    private const int Published = 1;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_типу_колонки_опублікованої_версії_відхиляється_тригером()
    {
        var doc = await PublishedAsync();

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET DataType = 3 WHERE Id = {doc.ColumnDefIds[0]}"));

        // ⚠ Тип колонки — структура: на нього спираються і вирази, і вже
        // записані дані. Змінити його в опублікованій версії означає заднім
        // числом перетлумачити історію (ФВ-7.1).
        Assert.Equal(50001, error.Number);
        Assert.Contains("CloneFrom", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_підпису_колонки_опублікованої_версії_дозволена()
    {
        var doc = await PublishedAsync();

        // Підпис — презентація. Заборонити його означало б вимагати клонування
        // версії заради виправленої одруки в заголовку.
        await ExecuteAsync(
            $"UPDATE cfg.ColumnDef SET HeaderL10n = N'{{\"en\":\"Виправлено\"}}' WHERE Id = {doc.ColumnDefIds[0]}");

        var header = await ScalarAsync<string>(
            $"SELECT HeaderL10n FROM cfg.ColumnDef WHERE Id = {doc.ColumnDefIds[0]}");
        Assert.Contains("Виправлено", header!, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_Ordinal_опублікованої_версії_дозволена()
    {
        var doc = await PublishedAsync();

        // Порядок — не ідентичність: на нього не можна посилатися, тому його
        // зміна нічого не ламає.
        await ExecuteAsync($"UPDATE cfg.ColumnDef SET Ordinal = 99 WHERE Id = {doc.ColumnDefIds[0]}");

        Assert.Equal(99, await ScalarAsync<int>(
            $"SELECT Ordinal FROM cfg.ColumnDef WHERE Id = {doc.ColumnDefIds[0]}"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміна_RowKey_опублікованої_версії_відхиляється()
    {
        var doc = await PublishedAsync();

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"UPDATE cfg.RowDef SET RowKey = N'ЗМІНЕНО' WHERE Id = {doc.RowDefIds[0]}"));

        // RowKey — стабільна бізнес-ідентичність рядка. Зміна ключа розриває
        // зв'язок із даними всіх минулих періодів.
        Assert.Equal(50002, error.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_формули_опублікованої_версії_відхиляється()
    {
        var doc = await PublishedAsync();
        var formulaId = await InsertFormulaAsync(doc);

        var error = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(
            $"DELETE FROM cfg.FormulaDef WHERE Id = {formulaId}"));

        // Тут тригер стоїть і на DELETE: зникла формула означає, що величина
        // за минулий період більше не відтворюється.
        Assert.Equal(50003, error.Number);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SaveChanges_на_таблиці_з_тригером_не_падає_бо_оголошено_HasTrigger()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        // ⚠ Саме те, заради чого потрібен HasTrigger(): EF Core 7+ пише через
        // OUTPUT, а на таблиці з тригером OUTPUT недоступний. Без оголошення
        // цей SaveChanges падає з «target table has triggers».
        await using var db = builder.CreateContext();
        var column = await db.ColumnDefs.FirstAsync(c => c.Id == doc.ColumnDefIds[0]);
        column.Reorder(42);
        var written = await db.SaveChangesAsync();

        Assert.Equal(1, written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Зміни_у_чернетці_тригер_не_блокує()
    {
        // Версія лишається Draft — саме в ній структуру й редагують.
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await ExecuteAsync($"UPDATE cfg.ColumnDef SET DataType = 3 WHERE Id = {doc.ColumnDefIds[0]}");
        await ExecuteAsync($"UPDATE cfg.RowDef SET RowKey = N'NEW_KEY' WHERE Id = {doc.RowDefIds[0]}");

        Assert.Equal(3, await ScalarAsync<byte>(
            $"SELECT DataType FROM cfg.ColumnDef WHERE Id = {doc.ColumnDefIds[0]}"));
    }

    /// <summary>Ланцюг із версією, переведеною в <c>Published</c>.</summary>
    private async Task<TestDocument> PublishedAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        // Статус ставимо прямим UPDATE, а не через Publish: use-case публікації
        // — це Етап 2 (він робить топологічне сортування формул), а тригер має
        // працювати від СТАНУ в базі, незалежно від того, хто його поставив.
        await ExecuteAsync(
            "UPDATE cfg.TemplateVersion SET Status = " + Published +
            ", PublishedAt = SYSUTCDATETIME(), PublishedByUserId = 1 " +
            $"WHERE Id = {doc.TemplateVersionId}");

        return doc;
    }

    private async Task<int> InsertFormulaAsync(TestDocument doc)
    {
        // Scope = 0 (Column) вимагає ColumnDefId — це стежить CK_Formula_Scope.
        await ExecuteAsync($"""
            INSERT INTO cfg.FormulaDef (TableDefId, ColumnDefId, Scope, Dialect, Expression)
            VALUES ({doc.TableDefId}, {doc.ColumnDefIds[1]}, 0, 0, N'1+1');
            """);

        return await ScalarAsync<int>(
            $"SELECT MAX(Id) FROM cfg.FormulaDef WHERE TableDefId = {doc.TableDefId}");
    }

    private async Task ExecuteAsync(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<T?> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value is null or DBNull ? default : (T)value;
    }
}
