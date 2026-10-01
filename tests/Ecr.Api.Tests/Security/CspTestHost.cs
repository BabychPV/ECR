// tests/Ecr.Api.Tests/Security/CspTestHost.cs

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using Ecr.Api.Controllers;
using Ecr.Api.Observability;
using Ecr.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// Мінімальний хост для перевірок CSP БЕЗ SQL Server: справжні
/// <see cref="SecurityHeadersMiddleware"/>, обмежувач частоти і
/// <see cref="CspReportController"/> — і нічого більше.
/// </summary>
/// <remarks>
/// ⚠ Контролер один, а не весь <c>Ecr.Api</c>: решті потрібна база, а тут перевіряється
/// саме приймач звітів. Журнал і лічильник збираються прямо з хоста, тож тест
/// бачить те, що побачив би оператор.
/// </remarks>
internal sealed class CspTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly MeterListener _listener = new();

    private CspTestHost(WebApplication app, CapturingLoggerProvider logs)
    {
        _app = app;
        Logs = logs;
    }

    /// <summary>Рядки журналу, записані хостом.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>Значення тегу <c>directive</c> кожного виміру <c>ecr.csp.violations</c>.</summary>
    public ConcurrentQueue<string> Violations { get; } = new();

    /// <summary>Клієнт із HTTP-адресою.</summary>
    public HttpClient Http => _app.GetTestClient();

    /// <summary>Клієнт із HTTPS-адресою: запит приходить із <c>IsHttps = true</c>.</summary>
    public HttpClient Https
    {
        get
        {
            var client = _app.GetTestClient();
            client.BaseAddress = new Uri("https://localhost/");
            return client;
        }
    }

    /// <summary>Піднімає хост із заданою конфігурацією.</summary>
    /// <param name="config">Ключі конфігурації (накладаються поверх порожньої).</param>
    /// <param name="withController"><c>true</c> — додати <see cref="CspReportController"/> і обмежувач.</param>
    public static async Task<CspTestHost> StartAsync(
        IReadOnlyDictionary<string, string?>? config = null, bool withController = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(config ?? new Dictionary<string, string?>());

        var logs = new CapturingLoggerProvider();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);

        if (withController)
        {
            builder.Services.AddSingleton<EcrMetrics>();
            builder.Services.AddEcrRateLimiting(builder.Configuration);
            builder.Services
                .AddControllers()
                .ConfigureApplicationPartManager(manager =>
                {
                    manager.ApplicationParts.Add(new AssemblyPart(typeof(CspReportController).Assembly));
                    manager.FeatureProviders.Clear();
                    manager.FeatureProviders.Add(new OnlyCspController());
                });
        }

        var app = builder.Build();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        if (withController)
        {
            // ⚠ Кінцеві точки, а не `app.Run`: термінальний middleware стоїть ПЕРЕД
            // автододаним `UseEndpoints`, і контролер до нього не дійшов би.
            app.UseRouting();
            app.UseRateLimiter();
            app.MapControllers();
            app.MapFallback(context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return context.Response.WriteAsync("ok");
            });
        }
        else
        {
            app.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return context.Response.WriteAsync("ok");
            });
        }

        var host = new CspTestHost(app, logs);
        host.ListenToMetrics();
        await app.StartAsync().ConfigureAwait(false);
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private void ListenToMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == EcrMetrics.MeterName && instrument.Name == EcrMetrics.CspViolations)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "directive")
                {
                    Violations.Enqueue((string)tag.Value!);
                }
            }
        });

        _listener.Start();
    }

    /// <summary>Постачальник ознак контролерів, що бачить лише приймач звітів.</summary>
    private sealed class OnlyCspController : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo)
            => typeInfo.AsType() == typeof(CspReportController);
    }
}

/// <summary>Збирає рядки журналу у пам'ять.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>Усе, що записано.</summary>
    public IReadOnlyCollection<LogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _entries);

    public void Dispose()
    {
    }

    /// <summary>Один записаний рядок.</summary>
    /// <param name="Category">Категорія логера.</param>
    /// <param name="Level">Рівень.</param>
    /// <param name="Message">Вже відформатований текст.</param>
    /// <param name="State">Структуровані поля.</param>
    internal sealed record LogEntry(
        string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State);

    private sealed class Capture(string category, ConcurrentQueue<LogEntry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    fields[pair.Key] = pair.Value;
                }
            }

            sink.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), fields));
        }
    }
}
