using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Транзакційна межа. Довгі транзакції заборонені (D-29): під RCSI вони
/// роздувають version store, і пік «останнього дня періоду» стає збоєм.
/// </summary>
[Collection("SqlServer")]
public sealed class UnitOfWorkTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вкладені_транзакції_заборонені()
    {
        await using var db = CreateContext();
        var uow = new UnitOfWork(db);

        await using var outer = await uow.BeginTransactionAsync(CancellationToken.None);

        // ⚠ SQL Server вкладених транзакцій не має: внутрішній BEGIN лише
        // збільшує лічильник, а внутрішній COMMIT не комітить нічого. EF це
        // не приховує і кидає — і добре, бо «вкладена транзакція», яка
        // насправді не транзакція, дає відкат, що зачіпає чужу роботу.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => uow.BeginTransactionAsync(CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Масовий_імпорт_іде_батчами_з_окремим_commit()
    {
        var doc = await BuildAsync();
        const int Batches = 3;
        const int PerBatch = 5;

        // Кожен батч — своя коротка транзакція. Одна довга на весь імпорт
        // тримала б версії рядків у tempdb від початку до кінця (D-29).
        for (var batch = 0; batch < Batches; batch++)
        {
            await using var db = CreateContext();
            var uow = new UnitOfWork(db);
            await using var tx = await uow.BeginTransactionAsync(CancellationToken.None);

            for (var i = 0; i < PerBatch; i++)
            {
                db.StyleDefs.Add(new StyleDef(
                    doc.TemplateVersionId, EcrCode.Create($"S{batch}_{i}_{doc.TableDefId}")));
            }

            await uow.SaveChangesAsync(CancellationToken.None);
            await ((IEcrTransaction)tx).CommitAsync(CancellationToken.None);
        }

        var saved = await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM cfg.StyleDef WHERE TemplateVersionId = {doc.TemplateVersionId}");

        Assert.Equal(Batches * PerBatch, saved);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відкат_повертає_стан_повністю()
    {
        var doc = await BuildAsync();
        var before = await StyleCountAsync(doc.TemplateVersionId);

        await using (var db = CreateContext())
        {
            var uow = new UnitOfWork(db);

            // ⚠ CommitAsync НЕ викликається навмисно. DisposeAsync обгортки
            // має відкотити: «забули закомітити» мусить означати відкат, а не
            // відкриту транзакцію, яка під RCSI псує життя всій базі.
            await using var tx = await uow.BeginTransactionAsync(CancellationToken.None);
            db.StyleDefs.Add(new StyleDef(doc.TemplateVersionId, EcrCode.Create($"ROLLBACK{doc.TableDefId}")));
            await uow.SaveChangesAsync(CancellationToken.None);
        }

        Assert.Equal(before, await StyleCountAsync(doc.TemplateVersionId));
    }

    private async Task<TestDocument> BuildAsync()
        => await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);

    private Task<int> StyleCountAsync(int templateVersionId)
        => ScalarAsync<int>($"SELECT COUNT(*) FROM cfg.StyleDef WHERE TemplateVersionId = {templateVersionId}");

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
