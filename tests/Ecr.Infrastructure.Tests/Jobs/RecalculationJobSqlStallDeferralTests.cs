// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobSqlStallDeferralTests.cs
using System.Reflection;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Зупинка бази (<c>SqlException -2</c> «Execution Timeout», <c>1205</c> «жертва дедлока») під перерахунком
/// ВІДКЛАДАЄ задачу (<see cref="JobDeferredException"/>), а не падає в <c>JobRetryPolicy</c> (3 ретраї 30/60/120 с
/// → <c>Failed</c>-прогони; 15.10, EcrLandUi). Прогін при цьому позначається <c>Failed</c> із текстом
/// «database error», а не «unexpected».
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати гілку <c>SqlStallClassifier.IsStall</c> у <c>RecalculationJob</c> — задача
/// кидає сирий <c>SqlException</c> (виконавець ретраїть), тести «відкладає» червоні; контрольний тест (547)
/// лишається зеленим. Стеля відкладень (<c>JobDeferral.MaxDeferral</c>, 30 хв → <c>Failed</c> з конвертом
/// «ресурс зайнятий») — спільна для будь-якого <see cref="JobDeferredException"/>: <c>JobDeferralCapTests</c>;
/// тут перевіряється, що відкладення несе ресурс документа, з яким стеля формує конверт.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobSqlStallDeferralTests(SqlServerFixture sql)
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData(-2)]
    [InlineData(1205)]
    public async Task Таймаут_команди_чи_дедлок_у_фазі_формул_відкладає_задачу_а_прогін_Failed_database_error(int number)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        var rows = RowsThrowing(Sql(number));

        await using var db = builder.CreateContext();
        var job = Job(db, rows, new RecordingRunner(null));

        var thrown = await Record.ExceptionAsync(
            () => job.ExecuteAsync(Request(document), NoOpProgress.Instance, CancellationToken.None));

        var deferred = Assert.IsType<JobDeferredException>(thrown);
        Assert.Equal(RecalculationDocumentLock.LockWaitDeferDelay(document.DocumentId), deferred.Delay);
        Assert.Equal(RecalculationDocumentLock.Resource(document.DocumentId), deferred.Resource);

        await using var verify = builder.CreateContext();
        var run = await verify.CalculationRuns.AsNoTracking().SingleAsync(r => r.ProjectId == document.ProjectId);
        Assert.Equal("Failed", run.Status);
        Assert.StartsWith("A database error", run.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// P2-1: маркування прогонів <c>Failed</c> теж падає зі зупинкою (<c>-2</c>) — відкладення має пріоритет:
    /// виняток маркування не підміняє початковий, задача не йде в <c>JobRetryPolicy</c> сирим <c>SqlException</c>.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_маркування_прогонів_Failed_не_ховає_відкладення()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .AddInterceptors(new FailMarkingSave(() => Sql(-2)))
            .Options;
        await using var db = new EcrDbContext(options);
        var job = Job(db, RowsThrowing(Sql(-2)), new RecordingRunner(null));

        var thrown = await Record.ExceptionAsync(
            () => job.ExecuteAsync(Request(document), NoOpProgress.Instance, CancellationToken.None));

        var deferred = Assert.IsType<JobDeferredException>(thrown);
        Assert.Equal(RecalculationDocumentLock.Resource(document.DocumentId), deferred.Resource);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Таймаут_у_гілці_оркестратора_всередині_AggregateException_теж_відкладає()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        var runner = new RecordingRunner(new AggregateException(new InvalidOperationException("branch A"), Sql(-2)));

        await using var db = builder.CreateContext();
        var job = Job(db, RowsEmpty(), runner);

        var thrown = await Record.ExceptionAsync(
            () => job.ExecuteAsync(Request(document), NoOpProgress.Instance, CancellationToken.None));

        Assert.IsType<JobDeferredException>(thrown);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Інший_SqlException_не_відкладається_а_летить_як_був()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        var original = Sql(547);

        await using var db = builder.CreateContext();
        var job = Job(db, RowsThrowing(original), new RecordingRunner(null));

        var thrown = await Record.ExceptionAsync(
            () => job.ExecuteAsync(Request(document), NoOpProgress.Instance, CancellationToken.None));

        Assert.Same(original, thrown);
    }

    /// <summary>Стеля таймауту перерахунку (<c>IRecalcCommandTimeout</c>) піднімається контекстом задачі раз на виконання.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Задача_піднімає_таймаут_команд_свого_контексту_до_стелі_перерахунку()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        var timeout = Substitute.For<IRecalcCommandTimeout>();

        await using var db = builder.CreateContext();
        var job = new RecalculationJob(
            db, new RecordingRunner(null), RunHandler(), FormulaService(RowsEmpty()), new TestClock(DateTime.UtcNow),
            commandTimeout: timeout);

        await job.ExecuteAsync(Request(document), NoOpProgress.Instance, CancellationToken.None);

        timeout.Received(1).Apply();
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Класифікатор_знаходить_таймаут_і_дедлок_у_дереві_і_не_чіпає_решту()
    {
        Assert.True(SqlStallClassifier.IsStall(Sql(-2)));
        Assert.True(SqlStallClassifier.IsStall(Sql(1205)));
        Assert.True(SqlStallClassifier.IsStall(new InvalidOperationException("x", new DbUpdateException("y", Sql(-2)))));
        Assert.True(SqlStallClassifier.IsStall(new AggregateException(new InvalidOperationException(), Sql(1205))));

        // 1222 — окрема гілка (стан зайнятості, `LockWaitGuard`), 547 — порушення обмеження.
        Assert.False(SqlStallClassifier.IsStall(Sql(1222)));
        Assert.False(SqlStallClassifier.IsStall(Sql(547)));
        Assert.False(SqlStallClassifier.IsStall(new InvalidOperationException("x")));
    }

    private static RecalculationJob Job(EcrDbContext db, IRowStore rows, ICalculationRunner runner)
        => new(db, runner, RunHandler(), FormulaService(rows), new TestClock(DateTime.UtcNow));

    private static RecalculationRequest Request(TestDocument document)
        => new(
            ProjectId: document.ProjectId,
            DocumentId: document.DocumentId,
            PeriodKey: document.PeriodKey.Value,
            TriggeredByUserId: null);

    private static IRowStore RowsEmpty()
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]));

        return rows;
    }

    private static IRowStore RowsThrowing(Exception error)
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Throws(error);

        return rows;
    }

    private static RecalculationService FormulaService(IRowStore rows)
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var header = Substitute.For<IDocumentHeaderStore>();
        header.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new(
            Substitute.For<ICellStore>(),
            rows,
            Substitute.For<IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IRegistryStore>(),
            header,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<ISheetEditGate>());
    }

    private static RunCalculationHandler RunHandler()
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            Substitute.For<ICalculationResultStore>(),
            Substitute.For<IBackgroundJobScheduler>(),
            Substitute.For<IUnitOfWork>(),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            new TestClock(DateTime.UtcNow),
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

    /// <summary><see cref="SqlException"/> із заданим номером: публічного конструктора немає, тож — рефлексією.</summary>
    private static SqlException Sql(int number)
    {
        var errorCtor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var args = errorCtor.GetParameters()
            .Select(p => p.Name == "infoNumber" ? number : Default(p.ParameterType))
            .ToArray();
        var error = (SqlError)errorCtor.Invoke(args);

        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(errors, [error]);

        var create = typeof(SqlException)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "CreateException"
                        && m.GetParameters().Select(p => p.ParameterType)
                            .SequenceEqual([typeof(SqlErrorCollection), typeof(string)]));

        var exception = (SqlException)create.Invoke(null, [errors, "16.0"])!;
        Assert.Equal(number, exception.Number);

        return exception;
    }

    private static object? Default(Type type)
        => type == typeof(string) ? "test"
            : type.IsValueType ? Activator.CreateInstance(type)
            : null;

    /// <summary>Валить збереження прогону, переведеного в <c>Failed</c> (маркування в catch задачі).</summary>
    private sealed class FailMarkingSave(Func<SqlException> error) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var hit = eventData.Context?.ChangeTracker.Entries<Ecr.Domain.Entities.Calculations.CalculationRun>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == "Failed") ?? false;

            return hit ? throw error() : base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class RecordingRunner(Exception? failure) : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => failure is null ? Task.FromResult(new ModuleProfile()) : Task.FromException<ModuleProfile>(failure);
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
