// tests/Ecr.Infrastructure.Tests/Jobs/FormulaRecalculationLockWaitDeferralTests.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// F1 (WW-02): інкрементний перерахунок після PATCH (<c>FormulaRecalculationJob</c>), у якого запис комірок не
/// дочекався блокування (<c>ECR-DOC-4091</c>, ключ <c>lockTimeout</c>), ВІДКЛАДАЄТЬСЯ з паузою
/// <see cref="RecalculationDocumentLock.LockWaitDeferDelay"/> і не падає в <c>JobRetryPolicy</c>.
/// </summary>
/// <remarks>
/// Справжній SQL і справжні сховища; відмову подає точка перемикання перед записом (хук
/// <c>IPeriodStore.FindPeriodStateAsync</c>, як у <see cref="FormulaRecalculationDocumentLockTests"/>), тож
/// реального очікування й сну немає: пауза — значення <see cref="JobDeferredException.Delay"/>.
/// <para>
/// МУТАЦІЯ: вимкнути гілку <c>IsLockWaitTimeout</c> у <c>FormulaRecalculationJob</c> — перший тест червоний
/// (летить <c>ConcurrencyConflictException</c>, який виконавець ретраїть 30/60/120 с).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class FormulaRecalculationLockWaitDeferralTests(SqlServerFixture sql)
{
    private const decimal Input = 21m;
    private const decimal StaleOutput = 20m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F1")]
    public async Task Вичерпане_очікування_блокування_відкладає_інкрементну_задачу_значення_не_змінені_повтор_пише()
    {
        var (doc, job, request, calls) = await ArrangeAsync(
            () => Conflict("ECR-DOC-4091", LockWaitGuard.MessageKey));

        var thrown = await Record.ExceptionAsync(() => job.ExecuteAsync(request, new Progress(), CancellationToken.None));

        var deferred = Assert.IsType<JobDeferredException>(thrown);
        Assert.Equal(RecalculationDocumentLock.LockWaitDeferDelay(doc.DocumentId), deferred.Delay);
        Assert.True(deferred.Delay > RecalculationDocumentLock.DeferDelay);
        Assert.Equal(RecalculationDocumentLock.Resource(doc.DocumentId), deferred.Resource);

        // Нічого не записано: обчислена колонка лишилась старою.
        Assert.Equal(StaleOutput, await LiveOutputAsync(doc));
        Assert.Equal(1, calls.Count);

        // Повтор виконавцем після паузи: блокування знято (хук більше не кидає) — число записано.
        await job.ExecuteAsync(request, new Progress(), CancellationToken.None);
        Assert.Equal(Input * 2, await LiveOutputAsync(doc));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F1")]
    public async Task Аркуш_подається_лишається_старим_відкладенням_з_короткою_паузою()
    {
        var (_, job, request, _) = await ArrangeAsync(
            () => Conflict("ECR-DOC-4091", "err.ECR-DOC-4091.sheetBeingSubmitted"));

        var deferred = await Assert.ThrowsAsync<JobDeferredException>(
            () => job.ExecuteAsync(request, new Progress(), CancellationToken.None));

        Assert.Equal(RecalculationDocumentLock.DeferDelay, deferred.Delay);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F1")]
    [InlineData("ECR-DOC-4091", "err.ECR-DOC-4091.structureChanged")]
    [InlineData("ECR-DOC-0409", "err.ECR-DOC-4091.lockTimeout")]
    public async Task Інший_конфлікт_не_відкладається_а_йде_старим_шляхом_ретраю(string code, string messageKey)
    {
        var (doc, job, request, _) = await ArrangeAsync(() => Conflict(code, messageKey));

        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => job.ExecuteAsync(request, new Progress(), CancellationToken.None));

        Assert.Equal(code, conflict.ErrorCode);
        Assert.Equal(StaleOutput, await LiveOutputAsync(doc));
    }

    private static ConcurrencyConflictException Conflict(string code, string messageKey)
        => new(code, "conflict", new Dictionary<string, object?> { ["messageKey"] = messageKey });

    /// <summary>Документ із входом 21 і застарілим виходом 20; хук кидає відмову лише при ПЕРШОМУ виклику.</summary>
    private async Task<(TestDocument Doc, FormulaRecalculationJob Job, FormulaRecalculationRequest Request, HookCalls Calls)>
        ArrangeAsync(Func<Exception> failure)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 1, rowMode: TableRowMode.Dynamic);

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, {Input}, 0, 0), " +
                    $"({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[2]}, {doc.TableDefId}, {StaleOutput}, 1, 0);");
            await command.ExecuteNonQueryAsync();
        }

        var calls = new HookCalls();
        var db = builder.CreateContext();
        var formulas = FormulaRecalculationDocumentLockTests.Formulas(
            db, doc,
            () => ++calls.Count == 1 ? Task.FromException(failure()) : Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(formulas);
        var job = ActivatorUtilities.CreateInstance<FormulaRecalculationJob>(services.BuildServiceProvider());

        var request = new FormulaRecalculationRequest(
            doc.TableInstanceId, doc.PeriodKey.Value, [new DirtyCell(doc.RowIds[0], doc.ColumnDefIds[1])]);

        return (doc, job, request, calls);
    }

    private async Task<decimal?> LiveOutputAsync(TestDocument doc)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {doc.RowIds[0]} AND ColumnDefId = {doc.ColumnDefIds[2]}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
    }

    private sealed class HookCalls
    {
        public int Count;
    }

    private sealed class Progress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
