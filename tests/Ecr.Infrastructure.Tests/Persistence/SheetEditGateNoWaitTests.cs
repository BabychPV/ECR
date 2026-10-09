using Ecr.Application.Errors;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// X6-02: <see cref="SheetEditGate.EnterEditNoWaitAsync"/> не стає в чергу <c>sp_getapplock</c>
/// за поданням, що вже чекає, — відмовляє одразу.
/// </summary>
/// <remarks>
/// ⛔ Предмет — та сама черга FIFO, через яку багатоаркушевий прогін перерахунку, чекаючи
/// наступний аркуш і тримаючи попередні, зупиняв подання й автозбереження інших аркушів.
/// Три справжні з'єднання: тримач спільного блокування (редактор), подання в черзі за ним
/// (виняткове) і писар без черги. Жодних пауз за годинником: подання вважається в черзі,
/// коли <c>sys.dm_tran_locks</c> показує його <c>WAIT</c>.
///
/// ⛔ Мутація: у <c>EnterEditNoWaitAsync</c> передати повний тайм-аут замість нуля — перший
/// тест стоїть за поданням до кінця очікування й червоніє на <c>WaitAsync</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetEditGateNoWaitTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "X6-02")]
    public async Task Спільне_без_черги_не_стає_за_поданням_що_чекає()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        // ── З'єднання 1: редактор тримає спільне блокування аркуша ──
        await using var editor = builder.CreateContext();
        await using var editorTransaction = await editor.Database.BeginTransactionAsync();
        await new SheetEditGate(editor).EnterEditAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);

        // ── З'єднання 2: подання стає в чергу за ним ──
        var submit = Task.Run(async () =>
        {
            await using var db = builder.CreateContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await new SheetEditGate(db).EnterSubmitAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);
            await transaction.RollbackAsync();
        });

        try
        {
            Assert.True(await WaitUntilQueuedOrDoneAsync(submit), "Подання не стало в чергу за спільним блокуванням.");

            // ── З'єднання 3: писар без черги — спільне сумісне з наданим, але в черзі вже виняткове ──
            await using var writer = builder.CreateContext();
            await using var writerTransaction = await writer.Database.BeginTransactionAsync();

            var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => new SheetEditGate(writer)
                    .EnterEditNoWaitAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(ErrorCodes.SheetBusy, error.ErrorCode);
            Assert.Equal("err.ECR-DOC-4091.sheetBeingSubmitted", error.Details!["messageKey"]);
            await writerTransaction.RollbackAsync();
        }
        finally
        {
            await editorTransaction.RollbackAsync();
            await submit.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "X6-02")]
    public async Task Вільний_аркуш_береться_без_черги_і_повертає_стан()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        // Інший редактор того самого аркуша — спільне з спільним сумісне, черги немає.
        await using var editor = builder.CreateContext();
        await using var editorTransaction = await editor.Database.BeginTransactionAsync();
        await new SheetEditGate(editor).EnterEditAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);

        await using var writer = builder.CreateContext();
        await using var writerTransaction = await writer.Database.BeginTransactionAsync();
        var status = await new SheetEditGate(writer)
            .EnterEditNoWaitAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);

        await writerTransaction.RollbackAsync();
        await editorTransaction.RollbackAsync();

        Assert.Equal(DocumentStatus.Draft, status);
    }

    /// <summary>
    /// Чекає, доки подання завершиться або стане в чергу на блокування аркуша; <c>true</c> — друге.
    /// </summary>
    private async Task<bool> WaitUntilQueuedOrDoneAsync(Task work)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (work.IsCompleted)
            {
                return false;
            }

            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = 'APPLICATION' AND request_status = 'WAIT'
                  AND resource_description LIKE '%sheet-ed%'
                """;
            if ((int)(await command.ExecuteScalarAsync())! > 0)
            {
                return true;
            }

            await Task.Delay(25);
        }

        return false;
    }
}
