// tests/Ecr.Api.Tests/Errors/ResponseStartedAndAbortedTests.cs
using Ecr.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Api.Tests.Errors;

/// <summary>
/// R5-E1: виняток після старту відповіді (E1-02).
/// </summary>
public sealed class ResponseStartedAndAbortedTests
{
    [Fact]
    [Trait("Requirement", "ФВ-6.11")]
    public async Task Виняток_після_старту_відповіді_рве_з_єднання_а_не_завершує_тіло_як_успішне()
    {
        var (context, lifetime, response) = Context(started: true);
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("збій посеред потокового CSV"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        // ⛔ Мутація: прибрати `context.Abort()` у гілці HasStarted → клієнт отримує обрізаний CSV зі статусом 200.
        Assert.True(lifetime.Aborted);
        Assert.Equal(200, response.StatusCode);
    }

    private static (DefaultHttpContext Context, Lifetime Lifetime, StartedResponse Response) Context(bool started)
    {
        var context = new DefaultHttpContext();
        var lifetime = new Lifetime();
        var response = new StartedResponse(started);
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Features.Set<IHttpResponseFeature>(response);
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));

        return (context, lifetime, response);
    }

    /// <summary>Відповідь; після старту сеттер статусу кидає, як у Kestrel.</summary>
    private sealed class StartedResponse(bool started) : IHttpResponseFeature
    {
        private int _status = 200;

        public int StatusCode
        {
            get => _status;
            set
            {
                if (started)
                {
                    throw new InvalidOperationException("StatusCode cannot be set because the response has already started.");
                }

                _status = value;
            }
        }

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

#pragma warning disable CS0618 // Член інтерфейсу застарілий, але обов'язковий до реалізації.
        public Stream Body { get; set; } = Stream.Null;
#pragma warning restore CS0618

        public bool HasStarted => started;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted
        {
            get => _cts.Token;
            set { }
        }

        public void Abort() => Aborted = true;

        public void Cancel() => _cts.Cancel();

        public void Dispose() => _cts.Dispose();
    }
}
