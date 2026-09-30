using System.Collections.Concurrent;
using System.Text.Json;
using Ecr.Api.Errors;
using Ecr.Api.Security;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// ФВ-5.24: обмежувач флуду, форма запису і відмова служби без впливу на <c>403</c>.
/// </summary>
/// <remarks>
/// Без бази: <see cref="IAuditWriter"/> — запис у пам'ять; те, що запис іде в ОКРЕМОМУ
/// scope і НЕЗАЛЕЖНИМ методом, видно з того, який метод і з якого scope викликано.
/// </remarks>
public sealed class AccessDenialAuditorTests
{
    private static readonly DateTime T0 = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Флуд_того_самого_користувача_й_маршруту_пишеться_раз_на_вікно_із_лічильником_пропущених()
    {
        var h = new Harness();

        for (var i = 0; i < 50; i++)
        {
            await h.DenyAsync(userId: 7, "/x/{id}");
        }

        Assert.Single(h.Written);

        // Наступне вікно: один запис, що каже, скільки відмов між ними не записано.
        h.Now = T0 + AccessDenialAuditor.DefaultWindow;
        await h.DenyAsync(userId: 7, "/x/{id}");

        Assert.Equal(2, h.Written.Count);
        var second = JsonDocument.Parse(h.Written.Last().DetailsJson!).RootElement;
        Assert.Equal(49, second.GetProperty("suppressed").GetInt32());
        Assert.False(JsonDocument.Parse(h.Written.First().DetailsJson!).RootElement.TryGetProperty("suppressed", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Інший_користувач_маршрут_чи_код_обмежується_окремо()
    {
        var h = new Harness();

        await h.DenyAsync(7, "/x/{id}");
        await h.DenyAsync(8, "/x/{id}");
        await h.DenyAsync(7, "/y/{id}");
        await h.DenyAsync(7, "/x/{id}", code: "ECR-SIM-0403");
        await h.DenyAsync(7, "/x/{id}", method: "POST");
        await h.DenyAsync(7, "/x/{id}");

        Assert.Equal(5, h.Written.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Паралельний_флуд_дає_рівно_один_запис()
    {
        var h = new Harness();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => h.DenyAsync(7, "/x/{id}"))));

        Assert.Single(h.Written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Невдалий_запис_не_витрачає_вікно_і_не_кидає()
    {
        var h = new Harness { FailWrites = true };

        var thrown = await Record.ExceptionAsync(() => h.DenyAsync(7, "/x/{id}"));
        Assert.Null(thrown);
        Assert.Empty(h.Written);
        Assert.Single(h.Logger.Warnings);

        // База повернулась — наступна відмова в тому самому вікні вже пишеться.
        h.FailWrites = false;
        await h.DenyAsync(7, "/x/{id}");
        Assert.Single(h.Written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Анонім_і_401_не_журналюються()
    {
        var h = new Harness();

        await h.DenyAsync(userId: null, "/x/{id}");
        await h.DenyAsync(7, "/x/{id}", code: "ECR-AUTH-0401");

        Assert.Empty(h.Written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Запис_іде_незалежним_методом_в_окремому_scope()
    {
        var h = new Harness();

        await h.DenyAsync(7, "/x/{id}");

        Assert.Equal(1, h.IndependentCalls);
        Assert.Equal(0, h.TransactionalCalls);
        Assert.Equal(1, h.ScopesCreated);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_У_подробицях_лише_білий_список_і_числові_ідентифікатори_з_маршруту()
    {
        var h = new Harness();

        await h.DenyAsync(
            7, "api/v1/projects/{projectId}/documents/{documentId}/cells",
            details: new Dictionary<string, object?>
            {
                ["permission"] = "Document.Edit",
                ["reason"] = "NoGrant",
                ["detail"] = "Рішення про доступ на комірку рядка 1001: Іван Іванов, ivan@example.com",
                ["cellValue"] = "42.5",
            },
            route: new RouteValueDictionary
            {
                ["projectId"] = "12",
                ["documentId"] = "345",
                ["code"] = "SecretRegistryCode",
                ["rowKey"] = "ivan@example.com",
            });

        var written = Assert.Single(h.Written);
        var json = written.DetailsJson!;
        var details = JsonDocument.Parse(json).RootElement;

        Assert.Equal("Document.Edit", details.GetProperty("permission").GetString());
        Assert.Equal("NoGrant", details.GetProperty("reason").GetString());
        Assert.Equal(12, details.GetProperty("projectId").GetInt64());
        Assert.Equal("documentId", details.GetProperty("resourceType").GetString());
        Assert.Equal(345, details.GetProperty("resourceId").GetInt64());
        Assert.Equal(7, written.ChangedByUserId);
        Assert.Equal("AccessDenied", written.EventType);
        Assert.Equal(T0, written.ChangedAt);

        foreach (var leak in new[] { "ivan", "Іван", "1001", "42.5", "SecretRegistryCode", "@" })
        {
            Assert.DoesNotContain(leak, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Речення_замість_ідентифікатора_права_не_потрапляє_в_журнал()
    {
        var h = new Harness();

        await h.DenyAsync(7, "/x", details: new Dictionary<string, object?> { ["permission"] = "Потрібне право X, рядок 5" });

        var details = JsonDocument.Parse(Assert.Single(h.Written).DetailsJson!).RootElement;
        Assert.False(details.TryGetProperty("permission", out _));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Без_ендпоінта_маршрут_не_береться_із_шляху()
    {
        var h = new Harness();

        await h.DenyAsync(7, route: null, rawPath: "/api/v1/projects/12/secret@example.com");

        var json = Assert.Single(h.Written).DetailsJson!;
        Assert.Equal("(unrouted)", JsonDocument.Parse(json).RootElement.GetProperty("route").GetString());
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Middleware_віддає_403_навіть_коли_журнал_кидає()
    {
        var auditor = Substitute.For<IAccessDenialAuditor>();
        auditor.RecordAsync(Arg.Any<HttpContext>(), Arg.Any<AccessDeniedException>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        var services = new ServiceCollection();
        services.AddSingleton(auditor);
        services.AddSingleton(Substitute.For<ICurrentUser>());
        await using var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        using var body = new MemoryStream();
        context.Response.Body = body;

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new AccessDeniedException("ECR-AUTH-0403", "no"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Contains("ECR-AUTH-0403", System.Text.Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
        await auditor.Received(1).RecordAsync(context, Arg.Any<AccessDeniedException>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-5.24")]
    public async Task ФВ_5_24_Middleware_не_журналює_401_і_інші_статуси()
    {
        foreach (var exception in new Exception[]
                 {
                     new AccessDeniedException("ECR-AUTH-0401", "anon"),
                     new NotFoundException("ECR-INT-0404", "nf"),
                 })
        {
            var auditor = Substitute.For<IAccessDenialAuditor>();
            var services = new ServiceCollection();
            services.AddSingleton(auditor);
            await using var provider = services.BuildServiceProvider();

            var context = new DefaultHttpContext { RequestServices = provider };
            context.Response.Body = new MemoryStream();

            await new ExceptionHandlingMiddleware(
                _ => throw exception, NullLogger<ExceptionHandlingMiddleware>.Instance).InvokeAsync(context);

            await auditor.DidNotReceiveWithAnyArgs().RecordAsync(default!, default!, default);
        }
    }

    /// <summary>Служба з підміненими scope, годинником і записом.</summary>
    private sealed class Harness
    {
        private readonly ConcurrentQueue<SecurityEventRecord> _written = new();
        private readonly AccessDenialAuditor _auditor;
        private readonly IClock _clock = Substitute.For<IClock>();
        private int _independent;
        private int _transactional;
        private int _scopes;

        public Harness()
        {
            _clock.UtcNow.Returns(_ => Now);

            var writer = Substitute.For<IAuditWriter>();
            writer.WriteIndependentSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Interlocked.Increment(ref _independent);
                    if (FailWrites)
                    {
                        return Task.FromException(new InvalidOperationException("db down"));
                    }

                    _written.Enqueue(call.Arg<SecurityEventRecord>());
                    return Task.CompletedTask;
                });
            writer.WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Interlocked.Increment(ref _transactional);
                    return Task.CompletedTask;
                });

            var services = new ServiceCollection();
            services.AddScoped(_ =>
            {
                Interlocked.Increment(ref _scopes);
                return writer;
            });
            var provider = services.BuildServiceProvider();

            Logger = new RecordingLogger();
            _auditor = new AccessDenialAuditor(
                provider.GetRequiredService<IServiceScopeFactory>(), _clock, Logger, AccessDenialAuditor.DefaultWindow);
        }

        public DateTime Now { get; set; } = T0;

        public bool FailWrites { get; set; }

        public RecordingLogger Logger { get; }

        public ConcurrentQueue<SecurityEventRecord> Written => _written;

        public int IndependentCalls => _independent;

        public int TransactionalCalls => _transactional;

        public int ScopesCreated => _scopes;

        public Task DenyAsync(
            int? userId, string template, string code = "ECR-AUTH-0403", string method = "GET",
            IReadOnlyDictionary<string, object?>? details = null, RouteValueDictionary? route = null)
            => DenyAsync(userId, template, code, method, details, route, rawPath: null, hasEndpoint: true);

        public Task DenyAsync(int? userId, string? route, string rawPath)
            => DenyAsync(userId, route, "ECR-AUTH-0403", "GET", null, null, rawPath, hasEndpoint: false);

        private async Task DenyAsync(
            int? userId, string? template, string code, string method,
            IReadOnlyDictionary<string, object?>? details, RouteValueDictionary? routeValues,
            string? rawPath, bool hasEndpoint)
        {
            var user = Substitute.For<ICurrentUser>();
            user.UserId.Returns(userId);

            var services = new ServiceCollection();
            services.AddSingleton(user);
            await using var provider = services.BuildServiceProvider();

            var context = new DefaultHttpContext { RequestServices = provider };
            context.Request.Method = method;
            context.Request.Path = rawPath ?? "/" + Guid.NewGuid().ToString("N");
            context.Request.QueryString = new QueryString("?email=leak@example.com");
            context.Items[Ecr.Api.Middleware.CorrelationIdMiddleware.ItemKey] = "corr-1";

            if (routeValues is not null)
            {
                foreach (var (k, v) in routeValues)
                {
                    context.Request.RouteValues[k] = v;
                }
            }

            if (hasEndpoint && template is not null)
            {
                context.SetEndpoint(new RouteEndpoint(
                    _ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, EndpointMetadataCollection.Empty, "t"));
            }

            await _auditor.RecordAsync(context, new AccessDeniedException(code, "denied", details), CancellationToken.None);
        }
    }

    /// <summary>Лічильник попереджень: збій запису має лишити рівно один.</summary>
    private sealed class RecordingLogger : ILogger<AccessDenialAuditor>
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => _warnings;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                _warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
