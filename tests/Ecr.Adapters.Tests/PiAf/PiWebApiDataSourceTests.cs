using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// <see cref="PiWebApiDataSource"/> (369 нетривіальних рядків: ретраї з
/// подвоєнням затримки на 5xx/таймаутах, ОКРЕМА гілка для 401/403 без жодного
/// повтору, часткова відмова батча з обрізаним інтервалом для наздоганяння,
/// розбір нечислових/переповнюючих значень AF, обчислення Quality) не мала
/// жодного тестового файлу (Q-256, аудит) — конкретна реалізація виконувалася
/// лише крізь <c>IExternalDataSource</c>-мок у <see cref="CollectionRunnerTests"/>,
/// НІКОЛИ насправді.
/// </summary>
/// <remarks>
/// ⚠ <see cref="PiWebApiDataSourceTimeoutTests"/> (Q-250) уже доводить, що
/// `HttpClient` цього класу має короткий таймаут і що зависання джерела дійсно
/// скасовується — тут це НЕ дублюється. Цей файл закриває решту: H-20
/// (401/403 не повторюється), ретраї на 5xx/мережевій відмові з реальним
/// подвоєнням затримки, обрізаний батч на стелі `MaxPoints`, значення поза
/// діапазоном `decimal`, Quality з `Good`/`Questionable` і формат заголовка
/// авторизації із секрету.
/// </remarks>
public sealed class PiWebApiDataSourceTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc);

    private const string ElementsSuccessJson = """{"Items":[]}""";
    private const string AttributeJson = """{"WebId":"W-1","DefaultUnitsName":"m3/h"}""";

    // ─────────────────────────────────────────────────────────────────────
    // (a) H-20: 401/403 — не повторюється жодного разу.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Регресійна перевірка H-20: 401/403 кидає
    /// <see cref="SourceAuthenticationException"/> з ПЕРШОЇ ж спроби — жодного
    /// повтору, навіть якщо <see cref="PiWebApiDataSource.MaxAttempts"/> &gt; 1.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутаційна перевірка (доведено вручну, не в CI): якщо в
    /// `PiWebApiDataSource.GetAsync` прибрати гілку `Unauthorized(...)` перед
    /// перевіркою `Retryable`, 401 почне класифікуватись як звичайна відмова —
    /// тест впаде на <c>Assert.Equal(1, handler.Requests.Count)</c> (запитів
    /// стане 3, а не 1) і на типі винятку (<see cref="BusinessRuleException"/>
    /// замість <see cref="SourceAuthenticationException"/>). Саме це і є H-20:
    /// «401 повторювався, ніби джерело просто лежить».
    /// </remarks>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task DiscoverAsync_401або403_КидаєSourceAuthenticationException_БезПовторів(
        HttpStatusCode status)
    {
        var handler = new QueueHttpMessageHandler().Enqueue(status);
        var sut = CreateSut(handler, out _);

        var thrown = await Assert.ThrowsAsync<SourceAuthenticationException>(
            () => sut.DiscoverAsync(1, CancellationToken.None));

        Assert.Equal(ErrorCodes.SourceAuthenticationRefused, thrown.ErrorCode);

        // Один-єдиний HTTP-запит — жодного повтору навіть при MaxAttempts == 3.
        Assert.Single(handler.Requests);
    }

    /// <summary>Інший 4xx (не автентифікація) теж не повторюється — але й не є `SourceAuthenticationException`.</summary>
    [Fact]
    public async Task DiscoverAsync_404_КидаєBusinessRuleException_БезПовторів()
    {
        var handler = new QueueHttpMessageHandler().Enqueue(HttpStatusCode.NotFound);
        var sut = CreateSut(handler, out _);

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => sut.DiscoverAsync(1, CancellationToken.None));

        Assert.Equal(ErrorCodes.SourceUnavailable, thrown.ErrorCode);
        Assert.Single(handler.Requests);
    }

    // ─────────────────────────────────────────────────────────────────────
    // (b) 5xx / мережева відмова — повторюється з подвоєнням затримки.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>408 (RequestTimeout) відповідає ретраю — і в результаті успіху.</summary>
    [Fact]
    public async Task DiscoverAsync_408_ПотімУспіх_ПовторюєІзЗатримкоюІВрештіВдається()
    {
        var handler = new QueueHttpMessageHandler()
            .Enqueue(HttpStatusCode.RequestTimeout)
            .Enqueue(HttpStatusCode.OK, ElementsSuccessJson);
        var sut = CreateSut(handler, out _);

        var stopwatch = Stopwatch.StartNew();
        var result = await sut.DiscoverAsync(1, CancellationToken.None);
        stopwatch.Stop();

        Assert.Empty(result);
        Assert.Equal(2, handler.Requests.Count);

        // ⛔ Мутаційна перевірка: без циклу повторів перший 408 кидав би винятком
        // одразу — виклик не дійшов би до другого запиту (Requests.Count == 1),
        // і затримки не було б узагалі.
        Assert.True(
            stopwatch.Elapsed >= PiWebApiDataSource.RetryDelay,
            $"мало пройти принаймні одну затримку ретраю ({PiWebApiDataSource.RetryDelay}), минуло {stopwatch.Elapsed}.");
    }

    /// <summary>
    /// Мережева відмова (<see cref="HttpRequestException"/>) — окрема гілка
    /// ретраю від коду статусу, і теж повторюється.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_HttpRequestException_ПовторюєІВрештіВдається()
    {
        var handler = new QueueHttpMessageHandler()
            .EnqueueException(new HttpRequestException("комутатор недоступний"))
            .Enqueue(HttpStatusCode.OK, ElementsSuccessJson);
        var sut = CreateSut(handler, out _);

        var result = await sut.DiscoverAsync(1, CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>
    /// Три відмови 5xx поспіль вичерпують <see cref="PiWebApiDataSource.MaxAttempts"/>
    /// і кидають <see cref="BusinessRuleException"/> — з подвоєнням затримки між
    /// спробами (2с, потім 4с), не однаковою паузою щоразу.
    /// </summary>
    [Fact]
    public async Task DiscoverAsync_500_ТричіПоспіль_ВичерпуєПовториІКидаєBusinessRuleException()
    {
        var handler = new QueueHttpMessageHandler()
            .Enqueue(HttpStatusCode.InternalServerError)
            .Enqueue(HttpStatusCode.InternalServerError)
            .Enqueue(HttpStatusCode.InternalServerError);
        var sut = CreateSut(handler, out _);

        var stopwatch = Stopwatch.StartNew();

        var thrown = await Assert.ThrowsAsync<BusinessRuleException>(
            () => sut.DiscoverAsync(1, CancellationToken.None));

        stopwatch.Stop();

        Assert.Equal(ErrorCodes.SourceUnavailable, thrown.ErrorCode);
        Assert.Equal(PiWebApiDataSource.MaxAttempts, handler.Requests.Count);

        // ⛔ Мутаційна перевірка на ПОДВОЄННЯ: рівно одна стала затримка (2с)
        // між трьома спробами дала б ~4с; подвоєння (2с + 4с) дає ~6с.
        // Поріг 5с відрізняє «доведено подвоєння» від «однакова пауза щоразу».
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromSeconds(5),
            $"подвоєння (2с+4с) мало дати ~6с затримки, минуло лише {stopwatch.Elapsed}.");
    }

    // ─────────────────────────────────────────────────────────────────────
    // (c) Обрізаний батч на стелі MaxPoints → FailedIntervals.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAsync_БатчНаСтеліMaxPoints_ЗаписуєОбрізанийІнтервалУFailedIntervals()
    {
        const int maxPoints = 2;
        var handler = new QueueHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, AttributeJson)
            .Enqueue(HttpStatusCode.OK, RecordedJson(From, maxPoints));
        var sut = CreateSut(handler, out _);

        var request = new CollectionRequest(1, 42, "tag", From, To, maxPoints);
        var result = await sut.ReadAsync(request, CancellationToken.None);

        Assert.Equal(maxPoints, result.Points.Count);
        Assert.Null(result.ErrorCode);

        var expectedFrom = result.Points[^1].Timestamp;
        Assert.Equal([new TimeInterval(expectedFrom, To)], result.FailedIntervals);
    }

    [Fact]
    public async Task ReadAsync_БатчМенгеЗаMaxPoints_НеВважаєтьсяОбрізаним()
    {
        var handler = new QueueHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, AttributeJson)
            .Enqueue(HttpStatusCode.OK, RecordedJson(From, count: 2));
        var sut = CreateSut(handler, out _);

        var request = new CollectionRequest(1, 42, "tag", From, To, MaxPoints: 5);
        var result = await sut.ReadAsync(request, CancellationToken.None);

        Assert.Equal(2, result.Points.Count);
        Assert.Empty(result.FailedIntervals);
    }

    /// <summary>
    /// Крайовий випадок з коментаря коду: порожній батч НЕ обрізаний навіть
    /// при <c>MaxPoints == 0</c> — інакше <c>points.Count &gt;= MaxPoints</c>
    /// (<c>0 &gt;= 0</c>) було б істинним завжди, і <c>points[^1]</c> впало б на
    /// порожньому списку.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ПорожнійБатчПриMaxPoints0_НеВважаєтьсяОбрізанимІНеПадає()
    {
        var handler = new QueueHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, AttributeJson)
            .Enqueue(HttpStatusCode.OK, RecordedJson(From, count: 0));
        var sut = CreateSut(handler, out _);

        var request = new CollectionRequest(1, 42, "tag", From, To, MaxPoints: 0);
        var result = await sut.ReadAsync(request, CancellationToken.None);

        Assert.Empty(result.Points);
        Assert.Empty(result.FailedIntervals);
    }

    // ─────────────────────────────────────────────────────────────────────
    // (d) Числове значення поза діапазоном decimal — зберігається як текст.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 39-значне число ширше за <see cref="decimal"/> (~29 значущих цифр) —
    /// не округлюється й не втрачається, а лишається текстом.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ЧисловеЗначенняЩоНеВміщуєтьсяВDecimal_ЗберігаєтьсяЯкТекстБезОкруглення()
    {
        const string overflowLiteral = "123456789012345678901234567890123456789";
        var recordedJson =
            "{\"Items\":[{\"Timestamp\":\"2026-01-01T00:00:00Z\",\"Value\":" + overflowLiteral + "}]}";

        var handler = new QueueHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, AttributeJson)
            .Enqueue(HttpStatusCode.OK, recordedJson);
        var sut = CreateSut(handler, out _);

        var request = new CollectionRequest(1, 42, "tag", From, To, MaxPoints: 10);
        var result = await sut.ReadAsync(request, CancellationToken.None);

        var point = Assert.Single(result.Points);
        Assert.Null(point.ValueNumeric);
        Assert.Equal(overflowLiteral, point.ValueString);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Quality: обчислюється з Good/Questionable.
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(",\"Good\":true,\"Questionable\":false", "Good")]
    [InlineData("", "Good")] // Good відсутній → добра за замовчуванням, Questionable відсутній.
    [InlineData(",\"Good\":false,\"Questionable\":false", "Bad")]
    [InlineData(",\"Good\":false,\"Questionable\":true", "Bad")] // Good=false переважає над Questionable.
    [InlineData(",\"Questionable\":true", "Questionable")]
    public async Task ReadAsync_Quality_ОбчислюєтьсяЗGoodІQuestionable(string extraFields, string expectedQuality)
    {
        var recordedJson =
            "{\"Items\":[{\"Timestamp\":\"2026-01-01T00:00:00Z\",\"Value\":1" + extraFields + "}]}";

        var handler = new QueueHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, AttributeJson)
            .Enqueue(HttpStatusCode.OK, recordedJson);
        var sut = CreateSut(handler, out _);

        var request = new CollectionRequest(1, 42, "tag", From, To, MaxPoints: 10);
        var result = await sut.ReadAsync(request, CancellationToken.None);

        var point = Assert.Single(result.Points);
        Assert.Equal(expectedQuality, point.Quality);
    }

    // ─────────────────────────────────────────────────────────────────────
    // (e) Заголовок авторизації з секрету: без пробілу → Bearer, із пробілом
    // → власна схема, порожній секрет → заголовка немає взагалі.
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Authorize_СекретБезПробілу_ДаєBearerСхемуЗУсімЗначеннямЯкПараметром()
    {
        var handler = new QueueHttpMessageHandler().Enqueue(HttpStatusCode.OK, ElementsSuccessJson);
        var sut = CreateSut(handler, out var secrets, secretValue: "eyJhbGciOi...token");

        await sut.DiscoverAsync(1, CancellationToken.None);

        var auth = handler.Requests[0].Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("eyJhbGciOi...token", auth.Parameter);
    }

    [Fact]
    public async Task Authorize_СекретЗПробілом_ВикористовуєСхемуНазванудоПробілу()
    {
        var handler = new QueueHttpMessageHandler().Enqueue(HttpStatusCode.OK, ElementsSuccessJson);
        var sut = CreateSut(handler, out var secrets, secretValue: "Basic dXNlcjpwYXNz");

        await sut.DiscoverAsync(1, CancellationToken.None);

        var auth = handler.Requests[0].Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth!.Scheme);
        Assert.Equal("dXNlcjpwYXNz", auth.Parameter);
    }

    [Fact]
    public async Task Authorize_ПорожнійСекрет_НеСтавитьЗаголовокAuthorizationВзагалі()
    {
        var handler = new QueueHttpMessageHandler().Enqueue(HttpStatusCode.OK, ElementsSuccessJson);
        var sut = CreateSut(handler, out var secrets, secretValue: null);

        await sut.DiscoverAsync(1, CancellationToken.None);

        // ⚠ Не «Basic <порожньо>» — саме відсутність заголовка (коментар коду:
        // вигаданий Basic з порожнім паролем дав би 401 і виглядав би як
        // недоступність джерела, а не як відсутність налаштування секрету).
        Assert.Null(handler.Requests[0].Headers.Authorization);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Спільне налаштування.
    // ─────────────────────────────────────────────────────────────────────

    private static PiWebApiDataSource CreateSut(
        HttpMessageHandler handler, out ISecretProvider secrets, string? secretValue = "")
    {
        var http = new HttpClient(handler) { BaseAddress = null };

        var store = Substitute.For<ICollectionStore>();
        var dataSource = new DataSource(
            EcrCode.Create("PIAF"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
            ExternalTransport.PiWebApi,
            "https://pi.example",
            "PiAf.Primary");
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);

        secrets = Substitute.For<ISecretProvider>();
        secrets.Find(Arg.Any<string>()).Returns(secretValue);

        return new PiWebApiDataSource(http, store, secrets);
    }

    private static string RecordedJson(DateTime from, int count)
    {
        var items = new StringBuilder();

        for (var i = 0; i < count; i++)
        {
            if (items.Length > 0)
            {
                items.Append(',');
            }

            var timestamp = from.AddMinutes(i).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            items.Append("{\"Timestamp\":\"").Append(timestamp).Append("\",\"Value\":").Append(i + 1).Append('}');
        }

        return "{\"Items\":[" + items + "]}";
    }

    /// <summary>Фейковий обробник HTTP: віддає заготовлені відповіді по черзі й записує кожен запит.</summary>
    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> responses = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public QueueHttpMessageHandler Enqueue(HttpStatusCode status, string? json = null)
        {
            responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status);
                if (json is not null)
                {
                    response.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                return response;
            });

            return this;
        }

        public QueueHttpMessageHandler EnqueueException(Exception exception)
        {
            responses.Enqueue(() => throw exception);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (responses.Count == 0)
            {
                throw new InvalidOperationException("У черзі немає заготовлених HTTP-відповідей.");
            }

            return Task.FromResult(responses.Dequeue()());
        }
    }
}
