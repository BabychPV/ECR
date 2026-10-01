// tests/Ecr.Infrastructure.Tests/Jobs/RegistryImpactRecalculationJobTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Батьківська задача перерахунку зачеплених (RT-25) на справжній базі: розкладає набір на
/// <see cref="ICalculationTrigger"/> лише для відкритих періодів і лише для названих документів.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>wanted.Contains</c> → <see cref="Тригер_лише_для_названих_документів"/> червоний;
/// набір з payload без звірки зі сховищем → <see cref="Закритий_період_не_ставиться_навіть_з_payload"/> червоний;
/// прибрати try/catch навколо <c>RequestAsync</c> → <see cref="Збій_одного_документа_не_перериває_решту_і_видно_у_завершенні"/>
/// червоний (другий документ не ставиться); прибрати <c>LogRequestFailed</c> або <c>InnerException</c> →
/// <see cref="Збій_постановки_журналюється_з_причиною_і_рахується_окремо_від_пропущених"/> червоний;
/// повернути <c>skipped = targets − queued</c> → він же червоний (провалений рахується «пропущеним»).
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryImpactRecalculationJobTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Тригер_лише_для_названих_документів()
    {
        var registryId = await NewRegistryAsync();
        var first = await ArrangeDocumentAsync(registryId, PeriodState.Open);
        var second = await ArrangeDocumentAsync(registryId, PeriodState.Open);
        var trigger = Trigger();

        await RunAsync(trigger, registryId, first.DocumentId);

        await trigger.Received(1).RequestAsync(first.DocumentId, first.PeriodKey, Arg.Any<CancellationToken>());
        await trigger.DidNotReceive().RequestAsync(second.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Закритий_період_не_ставиться_навіть_з_payload()
    {
        var registryId = await NewRegistryAsync();
        var closed = await ArrangeDocumentAsync(registryId, PeriodState.Closed);
        var trigger = Trigger();

        await RunAsync(trigger, registryId, closed.DocumentId);

        await trigger.DidNotReceiveWithAnyArgs().RequestAsync(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_одного_документа_не_перериває_решту_і_видно_у_завершенні()
    {
        var registryId = await NewRegistryAsync();
        var first = await ArrangeDocumentAsync(registryId, PeriodState.Open);
        var second = await ArrangeDocumentAsync(registryId, PeriodState.Open);
        var trigger = Trigger();
        trigger.RequestAsync(first.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw new InvalidOperationException("boom"));

        var act = () => RunAsync(trigger, registryId, first.DocumentId, second.DocumentId);

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        await trigger.Received(1).RequestAsync(second.DocumentId, second.PeriodKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_постановки_журналюється_з_причиною_і_рахується_окремо_від_пропущених()
    {
        var registryId = await NewRegistryAsync();
        var first = await ArrangeDocumentAsync(registryId, PeriodState.Open);
        var second = await ArrangeDocumentAsync(registryId, PeriodState.Open);
        var trigger = Trigger();
        var boom = new InvalidOperationException("boom");
        trigger.RequestAsync(first.DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(_ => throw boom);
        var logger = new ListLogger();
        var progress = Substitute.For<IJobProgress>();
        string? done = null;
        progress.ReportAsync(100, Arg.Do<string?>(m => done = m), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(trigger, registryId, logger, progress, first.DocumentId, second.DocumentId));

        // Причина збою — у журналі з номером документа, а не лише номер у тексті задачі.
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(boom, entry.Exception);
        Assert.Contains(first.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Message, StringComparison.Ordinal);

        // …і в ланцюжку винятку задачі.
        var inner = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Same(boom, Assert.Single(inner.InnerExceptions));

        // Підсумок: один поставлено, один провалено, «пропущених» (закритий період, поданий аркуш) немає.
        Assert.NotNull(done);
        Assert.Contains("\"queued\":\"1\"", done, StringComparison.Ordinal);
        Assert.Contains("\"failed\":\"1\"", done, StringComparison.Ordinal);
        Assert.Contains("\"skipped\":\"0\"", done, StringComparison.Ordinal);
    }

    private static ICalculationTrigger Trigger()
    {
        var trigger = Substitute.For<ICalculationTrigger>();
        trigger.RequestAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("recalc-1"));
        return trigger;
    }

    private Task RunAsync(ICalculationTrigger trigger, int registryId, params long[] documentIds)
        => RunAsync(trigger, registryId, logger: null, Substitute.For<IJobProgress>(), documentIds);

    private async Task RunAsync(
        ICalculationTrigger trigger,
        int registryId,
        ILogger<RegistryImpactRecalculationJob>? logger,
        IJobProgress progress,
        params long[] documentIds)
    {
        await using var db = Context();
        var job = new RegistryImpactRecalculationJob(new RegistryImpactStore(db), trigger, logger);

        await job.ExecuteAsync(
            new RegistryImpactRecalculationRequest(registryId, documentIds, "test"),
            progress,
            CancellationToken.None);
    }

    private async Task<int> NewRegistryAsync()
    {
        await using var db = Context();
        var registry = new RegistryDef(
            EcrCode.Create($"RJ{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Registry" }),
            isTemporal: false);

        // Правка після прогонів документів (`Now`): інакше вони не застарілі й не зачеплені.
        registry.MarkDataChanged(Now.AddHours(1));
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return registry.Id;
    }

    /// <summary>Документ із прогоном <c>Current</c>, чия методологія читає довідник.</summary>
    private async Task<(long DocumentId, PeriodKey PeriodKey)> ArrangeDocumentAsync(int registryId, PeriodState state)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.TransitionTo(PeriodState.Open, Now);
        if (state == PeriodState.Closed)
        {
            period.TransitionTo(PeriodState.Closed, Now);
        }

        var methodology = new Methodology(
            EcrCode.Create($"JM{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "m" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        db.RegistryUses.Add(RegistryUse.ForMethodologyFormula(version.Id, "F1", registryId, "X"));

        var run = new CalculationRun(document.ProjectId, document.PeriodKey.Value, null, Now, document.DocumentId);
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync();

        run.Complete("Succeeded", Now, null, null);
        run.MakeCurrent();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        await new CalculationResultStore(db, new TestClock(Now)).WriteResultsAsync(
            run.Id,
            [new CalculationOutput(
                document.DocumentId, "R-1",
                [new CalculationOutputValue(version.Id, null, "tons", 1m, unitId)],
                [])],
            CancellationToken.None);
        await db.SaveChangesAsync();

        return (document.DocumentId, document.PeriodKey);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Журнал, що зберігає записи для перевірки.</summary>
    private sealed class ListLogger : ILogger<RegistryImpactRecalculationJob>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
