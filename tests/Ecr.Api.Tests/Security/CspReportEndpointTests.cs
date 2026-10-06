// tests/Ecr.Api.Tests/Security/CspReportEndpointTests.cs

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ecr.Api.Controllers;
using Ecr.TestKit;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <c>POST /api/v1/csp-report</c>: анонімний приймач звітів браузера (<c>S14</c>).
/// </summary>
/// <remarks>
/// ⛔ Ендпоінт відкритий будь-кому, тому перевіряється не лише «204 на добрий звіт»,
/// а й усе, чим анонім міг би зашкодити: розмір, форма тіла, тип вмісту, частота, і
/// — головне — ЩО саме потрапляє в журнал (адреси без query/fragment).
///
/// ⚠ Без SQL Server: <see cref="CspTestHost"/> піднімає лише контролер і обмежувач.
/// </remarks>
public sealed class CspReportEndpointTests
{
    private static readonly Uri Route = new(CspReportController.RoutePath, UriKind.Relative);

    private const string Legacy = """
        {"csp-report":{"document-uri":"https://ecr.example/documents/42?token=SECRET-TOKEN#frag",
        "violated-directive":"script-src-elem 'self'","effective-directive":"script-src-elem",
        "blocked-uri":"https://cdn.evil.example/x.js?session=SECRET-SESSION",
        "source-file":"https://ecr.example/assets/index-abc.js?v=SECRET-VERSION","line-number":17,
        "original-policy":"default-src 'self'; script-src 'self'"}}
        """;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Валідний_звіт_дає_204_один_рядок_журналу_і_лічильник_з_директивою()
    {
        await using var host = await CspTestHost.StartAsync(withController: true).ConfigureAwait(true);

        var response = await Post(host, Legacy, "application/csp-report").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync().ConfigureAwait(true));

        // Рівно один рядок про порушення — «один структурований рядок на звіт».
        var entry = Assert.Single(host.Logs.Entries, e => e.Message.StartsWith("CSP violation", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, entry.Level);
        Assert.Equal("script-src-elem", entry.State["Directive"]);
        Assert.Equal("script-src-elem 'self'", entry.State["ViolatedDirective"]);
        Assert.Equal("https://cdn.evil.example/x.js", entry.State["BlockedUri"]);
        Assert.Equal("https://ecr.example/documents/42", entry.State["DocumentUri"]);
        Assert.Equal("https://ecr.example/assets/index-abc.js", entry.State["SourceFile"]);
        Assert.Equal(17, entry.State["LineNumber"]);

        Assert.Equal(["script-src-elem"], host.Violations.ToArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task У_журнал_не_потрапляє_ні_query_ні_fragment_ні_userinfo()
    {
        await using var host = await CspTestHost.StartAsync(withController: true).ConfigureAwait(true);

        var body = Legacy.Replace(
            "https://cdn.evil.example/x.js?session=SECRET-SESSION",
            "https://user:PASSWORD@cdn.evil.example/x.js?session=SECRET-SESSION#SECRET-HASH",
            StringComparison.Ordinal);

        await Post(host, body, "application/csp-report").ConfigureAwait(true);

        // ⛔ Мутаційний доказ: передати в LogViolation сирі значення з JSON (без StripUrl)
        // — падає цей тест: `token=SECRET-TOKEN` доїжджає до журналу разом з адресою.
        var everything = string.Join(
            "\n",
            host.Logs.Entries.Select(e => e.Message + " " + string.Join(" ", e.State.Values)));

        foreach (var secret in new[] { "SECRET-TOKEN", "SECRET-SESSION", "SECRET-VERSION", "SECRET-HASH", "PASSWORD", "frag" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("?", string.Join(" ", host.Logs.Entries
            .Where(e => e.Message.StartsWith("CSP violation", StringComparison.Ordinal))
            .Select(e => e.Message)), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Reporting_API_масив_логує_лише_csp_violation()
    {
        await using var host = await CspTestHost.StartAsync(withController: true).ConfigureAwait(true);

        const string body = """
            [{"type":"csp-violation","url":"https://ecr.example/","body":{
              "documentURL":"https://ecr.example/a?x=1","blockedURL":"inline",
              "effectiveDirective":"style-src-elem","lineNumber":3,"sourceFile":"https://ecr.example/s.js?q=1"}},
             {"type":"deprecation","body":{"id":"X"}}]
            """;

        var response = await Post(host, body, "application/reports+json").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var entry = Assert.Single(host.Logs.Entries, e => e.Message.StartsWith("CSP violation", StringComparison.Ordinal));
        Assert.Equal("style-src-elem", entry.State["Directive"]);
        Assert.Equal("inline", entry.State["BlockedUri"]);
        Assert.Equal("https://ecr.example/a", entry.State["DocumentUri"]);
        Assert.Equal(["style-src-elem"], host.Violations.ToArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Довільна_директива_йде_в_метрику_як_other()
    {
        await using var host = await CspTestHost.StartAsync(withController: true).ConfigureAwait(true);

        // ⛔ Тег приходить від анонімного джерела: вільний рядок у ньому — необмежена
        // кількість часових рядів, якою роздувають пам'ять експортера.
        var body = Legacy
            .Replace("script-src-elem 'self'", "evil-" + Guid.NewGuid().ToString("N") + " x", StringComparison.Ordinal)
            .Replace("\"effective-directive\":\"script-src-elem\",", string.Empty, StringComparison.Ordinal);

        await Post(host, body, "application/csp-report").ConfigureAwait(true);

        Assert.Equal(["other"], host.Violations.ToArray());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    [InlineData("application/csp-report")]
    [InlineData("application/reports+json")]
    [InlineData("application/json")]
    [InlineData("application/csp-report; charset=utf-8")]
    public async Task Дозволені_типи_вмісту_приймаються(string contentType)
    {
        await using var host = await CspTestHost.StartAsync(withController: true).ConfigureAwait(true);

        var response = await Post(host, Legacy, contentType).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task Інший_тип_вмісту_дає_415_і_нічого_не_логує(string contentType)
    {
        await using var host = await CspTestHost.StartAsync(withController: true).ConfigureAwait(true);

        var response = await Post(host, Legacy, contentType).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Message.StartsWith("CSP violation", StringComparison.Ordinal));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("{\"a\":1}")]
    [InlineData("{\"csp-report\":\"рядок\"}")]
    [InlineData("42")]
    [InlineData("{\"csp-report\":{")]
    public async Task Невалідне_тіло_дає_400_і_нічого_не_логує(string body)
    {
        await using var host = await CspTestHost.StartAsync(withController: true).ConfigureAwait(true);

        var response = await Post(host, body, "application/csp-report").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Violations);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Тіло_понад_8_КБ_дає_413_а_рівно_8_КБ_приймається()
    {
        await using var host = await CspTestHost.StartAsync(withController: true).ConfigureAwait(true);

        // Точно на межі: JSON, доведений пробілами до рівно 8192 байтів.
        var exact = PadTo(Legacy, CspReportController.MaxBodyBytes);
        var over = PadTo(Legacy, CspReportController.MaxBodyBytes + 1);

        Assert.Equal(CspReportController.MaxBodyBytes, Encoding.UTF8.GetByteCount(exact));

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Post(host, exact, "application/csp-report").ConfigureAwait(true)).StatusCode);

        // ⛔ Мутаційний доказ: підняти MaxBodyBytes чи прибрати перевірку — падає.
        Assert.Equal(
            HttpStatusCode.RequestEntityTooLarge,
            (await Post(host, over, "application/csp-report").ConfigureAwait(true)).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Тіло_без_Content_Length_теж_обмежене()
    {
        await using var host = await CspTestHost.StartAsync(withController: true).ConfigureAwait(true);

        // ⚠ Chunked: розмір наперед невідомий, і `Content-Length`-перевірка нічого не бачить.
        // Без читання з обмеженням такий запит витягнув би в пам'ять довільний обсяг.
        var bytes = Encoding.UTF8.GetBytes(PadTo(Legacy, CspReportController.MaxBodyBytes * 4));
        using var content = new UnknownLengthContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/csp-report");

        var response = await host.Http.PostAsync(Route, content).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public async Task Обмеження_частоти_за_адресою_дає_429_без_тіла()
    {
        await using var host = await CspTestHost.StartAsync(
            new Dictionary<string, string?> { ["Security:RateLimit:CspReportPermitPerMinute"] = "3" },
            withController: true).ConfigureAwait(true);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(
                HttpStatusCode.NoContent,
                (await Post(host, Legacy, "application/csp-report").ConfigureAwait(true)).StatusCode);
        }

        // ⛔ Мутаційний доказ: прибрати гілку `IsCspReport` у GlobalLimiter — четвертий
        // запит знову 204, а журнал під «зацикленою» вкладкою росте без меж.
        var rejected = await Post(host, Legacy, "application/csp-report").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Empty(await rejected.Content.ReadAsStringAsync().ConfigureAwait(true));
        Assert.Equal(3, host.Violations.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "A3-03")]
    public async Task Скасований_клієнтом_запит_не_вилітає_з_контролера_і_не_дає_помилки_в_журналі()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await CspTestHost.StartAsync(
            withController: true,
            wrapBody: inner => new SignalOnReadStream(inner, reading)).ConfigureAwait(true);

        using var cts = new CancellationTokenSource();
        using var content = new HangingContent();
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/csp-report");

        var send = host.Http.PostAsync(Route, content, cts.Token);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
        await cts.CancelAsync().ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send).ConfigureAwait(true);
        await Task.Delay(200).ConfigureAwait(true);

        // ⛔ Мутаційний доказ: прибрати `catch (OperationCanceledException)` у контролері —
        // виняток вилітає в конвеєр, у проді його пише ExceptionHandlingMiddleware як
        // «Необроблений виняток» рівня Error; тут це лічильник Unhandled.
        Assert.Equal(0, host.Unhandled);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "A3-03")]
    public async Task Обірване_тіло_запиту_дає_400_а_не_необроблений_виняток()
    {
        await using var host = await CspTestHost.StartAsync(
            withController: true,
            wrapBody: _ => new ThrowingStream(
                new Microsoft.AspNetCore.Http.BadHttpRequestException("Unexpected end of request content"))).ConfigureAwait(true);

        var response = await Post(host, Legacy, "application/csp-report").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Unhandled);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "A3-03")]
    public async Task Справжній_збій_читання_не_ковтається()
    {
        await using var host = await CspTestHost.StartAsync(
            withController: true,
            wrapBody: _ => new ThrowingStream(new InvalidOperationException("boom"))).ConfigureAwait(true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Post(host, Legacy, "application/csp-report")).ConfigureAwait(true);
        Assert.Equal(1, host.Unhandled);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "S14")]
    public void Ендпоінт_анонімний_і_не_читає_бази()
    {
        // Браузер шле звіт без автентифікації, а політика діє й на сторінці входу.
        var action = typeof(CspReportController).GetMethod(nameof(CspReportController.Report))!;
        Assert.NotEmpty(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false));

        // ⚠ У конструкторі — лише журнал і метрики: жодного DbContext, репозиторію,
        // обробника застосунку. «Ендпоінт НЕ пише в БД» стає властивістю типу.
        var parameters = typeof(CspReportController).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["EcrMetrics", "ILogger`1"], parameters);
    }

    private static async Task<HttpResponseMessage> Post(CspTestHost host, string body, string contentType)
    {
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return await host.Http.PostAsync(Route, content).ConfigureAwait(false);
    }

    /// <summary>Доповнює JSON кінцевими пробілами до рівно <paramref name="bytes"/> байтів (ASCII).</summary>
    private static string PadTo(string json, int bytes)
    {
        var compact = json.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);

        // Тіло — ASCII, тож символи дорівнюють байтам.
        Assert.True(Encoding.UTF8.GetByteCount(compact) <= bytes);
        return compact + new string(' ', bytes - Encoding.UTF8.GetByteCount(compact));
    }

    /// <summary>Тіло, що кидає заданий виняток при читанні.</summary>
    private sealed class ThrowingStream(Exception error) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw error;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => throw error;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Сигналізує про перше читання й далі читає справжнє тіло.</summary>
    private sealed class SignalOnReadStream(Stream inner, TaskCompletionSource signal) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            signal.TrySetResult();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Вміст, що пише початок тіла й зависає — клієнт «йде» посеред запиту.</summary>
    private sealed class HangingContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(new byte[] { (byte)'{' }).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    /// <summary>Вміст без відомої довжини — іде як <c>Transfer-Encoding: chunked</c>.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(bytes, 0, bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
