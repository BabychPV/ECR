using System.Diagnostics.Metrics;
using Ecr.Api.Errors;
using Ecr.Api.Observability;
using Ecr.Application.Errors;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>ecr.conflict.count</c> (ФВ-12.7, НФ-8.6.2): <c>ConcurrencyConflictException</c> на виході
/// конвеєра дає +1, інші відмови — нуль. Реальний MeterListener, ізольований за фабрикою Meter.
/// </summary>
/// <remarks>Мутація: прибрати <c>RecordConflict</c> у <c>ExceptionHandlingMiddleware.WriteAsync</c> — перший червоний.</remarks>
public sealed class ConflictMetricTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Конфлікт_паралельного_редагування_дає_один_ecr_conflict_count()
    {
        var count = await CountAsync(() => throw new ConcurrencyConflictException("ECR-DOC-0409", "конфлікт"));

        Assert.Equal(1, count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Інша_відмова_не_збільшує_ecr_conflict_count()
    {
        var count = await CountAsync(() => throw new InvalidOperationException("збій"));

        Assert.Equal(0, count);
    }

    private static async Task<long> CountAsync(Action throwing)
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<EcrMetrics>();
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();

        long total = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == EcrMetrics.ConflictCount && ReferenceEquals(instrument.Meter.Scope, factory))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref total, value));
        listener.Start();
        _ = provider.GetRequiredService<EcrMetrics>();
        listener.RecordObservableInstruments();

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionHandlingMiddleware(
            _ => { throwing(); return Task.CompletedTask; }, NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);
        return Interlocked.Read(ref total);
    }
}
