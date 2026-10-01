// tests/Ecr.Application.Tests/Integration/DataSourceHandlersTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Конфігурація джерел даних (<c>BE-21</c>, ФВ-14.3) після рішення людини на
/// <c>Q15-06</c>: сховища секретів немає, джерела ходять під службовим
/// обліковим записом.
/// </summary>
public sealed class DataSourceHandlersTests
{
    private const int Actor = 11;
    private const string Endpoint = "https://pi.corp.example/piwebapi";
    private const string Reason = "Переїхав сервер AF, звіряємо доступ.";

    /// <summary>Значення секрету, яке середовище дає під джерело.</summary>
    private const string SecretValue = "IntegratedFallback-9d41";

    private readonly FakeStore _store = new();
    private readonly FakeSecrets _secrets = new();
    private readonly FakeAdapter _adapter = new();
    private readonly SourceProbeGate _gate = new();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly List<SecurityEventRecord> _events = [];
    private readonly CapturingLogger _log = new();

    public DataSourceHandlersTests()
    {
        _user.UserId.Returns(Actor);
        Allow("Integration.Manage", "Integration.View");

        _audit.WriteSecurityEventAsync(
                Arg.Do<SecurityEventRecord>(_events.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task Без_свого_права_жодна_дія_не_виконується()
    {
        var source = Add("PI_MAIN");

        // ⛔ Читання і правка розведені: `Integration.View` відкриває перелік і
        // не відкриває нічого іншого. Без цього твердження право на перегляд
        // могло б мовчки стати правом на зміну конфігурації збору.
        Allow("Integration.View");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().CreateAsync("NEW", Named(), ExternalTransport.PiWebApi, Endpoint, null, null, null, null, default));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Delete().HandleAsync(source.Id, Version(source), default));
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Test().HandleAsync(source.Id, Reason, default));

        // …а перелік `Integration.View` відкриває.
        Assert.Single(await List().HandleAsync(default));

        // Без обох прав перелік закритий, і відмова називає право перегляду.
        Allow("Integration.EditSchedule");

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => List().HandleAsync(default));
        Assert.Equal("Integration.View", denied.Details!["permission"]);

        Assert.Single(_store.Sources);
        Assert.Empty(_store.Removed);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task Integration_Manage_без_View_бачить_перелік_зєднань()
    {
        // ⛔ Хто заводить і тестує з'єднання, мусить бачити їхній перелік:
        // інакше кнопка «New connection» стоїть над відмовою 403.
        Add("PI_MAIN");
        Allow("Integration.Manage");

        var rows = await List().HandleAsync(default);

        Assert.Equal("PI_MAIN", Assert.Single(rows).Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task Адреса_з_обліковими_даними_відхиляється_і_в_базу_не_потрапляє()
    {
        // ⛔ Це і є «write-only» у тому вигляді, який лишився після Q15-06:
        // сховища секретів немає, отже єдиний спосіб покласти пароль у базу —
        // вписати його в НЕСЕКРЕТНЕ поле. Для транспорту Sql адреса і є рядком
        // з'єднання, тож форма виглядає цілком природною.
        string[] carriers =
        [
            "Server=flert;Database=Vol;User Id=svc;Password=hunter2",
            "Server=flert;Database=Vol;Pwd = hunter2",
            "https://prod.logic.azure.com/workflows/abc?sig=Zm9v",
            "https://svc:hunter2@pi.corp.example/piwebapi",
        ];

        foreach (var address in carriers)
        {
            var refused = await Assert.ThrowsAsync<BusinessRuleException>(
                () => Save().CreateAsync("FLERT", Named(), ExternalTransport.Sql, address, null, null, null, null, default));

            Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
            Assert.Equal("err.ECR-REQ-0422.dataSourceEndpointCarriesSecret", refused.Details!["messageKey"]);
            Assert.Equal("endpoint", refused.Details!["field"]);

            // ⛔ Відмова не повторює введеного: інакше пароль, який ми щойно
            // відмовилися зберігати, поїхав би в журнал разом із її текстом.
            Assert.DoesNotContain("hunter2", refused.Message, StringComparison.Ordinal);
        }

        // Та сама перевірка на ЗАПАСНІЙ адресі — і на правці, не лише створенні.
        var existing = Add("PI_MAIN");

        var spare = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().UpdateAsync(
                existing.Id, Named(), ExternalTransport.PiWebApi, Endpoint,
                "https://svc:hunter2@pi2.corp.example", null, null, true, Version(existing), null, false, default));

        // Відмова називає поле, у якому облікові дані, — не вміст.
        Assert.Equal("secondaryEndpoint", spare.Details!["field"]);

        // Несуть обидві — називається перша за порядком перевірки.
        var both = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(
                "BOTH", Named(), ExternalTransport.Sql, "Server=a;Password=x", "Server=b;Password=y",
                null, null, null, default));
        Assert.Equal("endpoint", both.Details!["field"]);

        Assert.Empty(_store.Added);
        Assert.Equal(Endpoint, existing.Endpoint);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task Перелік_віддає_ознаку_секрету_а_не_його_значення()
    {
        var source = Add("PI_MAIN");
        _secrets.Values["DataSource.PI_MAIN"] = SecretValue;

        var rows = await List().HandleAsync(default);
        var row = Assert.Single(rows);

        Assert.True(row.HasSecret);
        Assert.Equal(source.Code, row.Code);

        // ⛔ Твердження проти ВСЬОГО запису, а не проти переліку полів: нове
        // поле, у яке хтось покладе `secrets.Find(...)`, завалить саме цей
        // рядок. Перелік полів рухався б разом із записом і не тримав би нічого.
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(row), StringComparison.Ordinal);

        // Ім'я секрету — не значення (ФВ-6.11): воно теж не їде клієнтові.
        Assert.DoesNotContain("DataSource.PI_MAIN", JsonSerializer.Serialize(row), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task Поки_перевірка_зєднання_йде_друга_на_те_саме_джерело_дає_409()
    {
        var source = Add("PI_MAIN");
        var other = Add("FLERT");

        // ⚠ Подвійник адаптера тримають «усередині» проби — мережі тут немає
        // взагалі, і саме тому перевірку одночасності можна писати детерміновано.
        var first = Test().HandleAsync(source.Id, Reason, CancellationToken.None);
        await _adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Test().HandleAsync(source.Id, Reason, CancellationToken.None));

        Assert.Equal("ECR-JOB-0409", refused.ErrorCode);
        Assert.Equal("err.ECR-JOB-0409.dataSourceTestRunning", refused.Details!["messageKey"]);
        Assert.Equal("PI_MAIN", refused.Details!["code"]);

        // ⛔ Контроль: ворота стережуть ДЖЕРЕЛО, а не кнопку. Без цього
        // твердження обробник, що відмовляє на будь-яку активну пробу, виглядав
        // би правильним — і одна перевірка закривала б усі джерела системи.
        var neighbour = Test().HandleAsync(other.Id, Reason, CancellationToken.None);

        _adapter.Release.SetResult();
        var done = await first;

        Assert.True(done.Ok);
        Assert.Equal(1, done.Entities);
        Assert.True((await neighbour).Ok);

        // Ворота звільняються після проби: наступна на те саме джерело проходить.
        Assert.True((await Test().HandleAsync(source.Id, Reason, CancellationToken.None)).Ok);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task Перевірка_потребує_причини_і_пише_її_в_журнал_безпеки()
    {
        var source = Add("PI_MAIN");
        _adapter.Release.SetResult();

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Test().HandleAsync(source.Id, "   ", default));

        Assert.Equal("err.ECR-REQ-0422.dataSourceTestReason", refused.Details!["messageKey"]);
        Assert.Empty(_events);

        // ⚠ 401 символ ЛІТЕРАЛОМ, а не `MaxReasonLength + 1`: твердження проти
        // константи з того самого модуля поїхало б разом із нею.
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Test().HandleAsync(source.Id, new string('x', 401), default));
        Assert.Equal(400, TestDataSourceConnectionHandler.MaxReasonLength);

        var result = await Test().HandleAsync(source.Id, $"  {Reason}  ", default);

        Assert.True(result.Ok);

        var recorded = Assert.Single(_events);
        Assert.Equal(TestDataSourceConnectionHandler.EventType, recorded.EventType);
        Assert.Equal(Actor, recorded.ChangedByUserId);

        // ⚠ Подробиці РОЗБИРАЮТЬСЯ, а не шукаються підрядком: серіалізатор
        // екранує кирилицю, тож пошук рядка був би зеленим лише для латиниці.
        var details = JsonDocument.Parse(recorded.DetailsJson ?? "{}").RootElement;
        Assert.Equal(Reason, details.GetProperty("reason").GetString());
        Assert.True(details.GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task Джерело_із_сутностями_збору_не_видаляється_а_вимикається()
    {
        var source = Add("PI_MAIN");
        _store.Usage[source.Id] = new DataSourceUsage(SourceEntities: 12, CollectionSchedules: 3);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Delete().HandleAsync(source.Id, Version(source), default));

        // ⛔ Заборона, а не каскад: каскад стер би `ext.RawDataPoint` і журнал
        // покриття, за якими вже пораховані й підписані документи.
        Assert.Equal("ECR-JOB-0409", refused.ErrorCode);
        Assert.Equal("err.ECR-JOB-0409.dataSourceInUse", refused.Details!["messageKey"]);
        Assert.Equal("12", refused.Details!["sourceEntities"]);
        Assert.Equal("3", refused.Details!["collectionSchedules"]);
        Assert.Empty(_store.Removed);

        // Оборотна дія на її місці: збір спиняє `isActive = false`.
        var disabled = await Save().UpdateAsync(
            source.Id, Named(), ExternalTransport.PiWebApi, Endpoint, null, null, null, false, Version(source), null, false, default);

        Assert.False(disabled.IsActive);
        Assert.Equal(12, disabled.SourceEntities);

        // А джерело, на яке ніщо не спирається, прибирається.
        _store.Usage[source.Id] = new DataSourceUsage(0, 0);
        await Delete().HandleAsync(source.Id, Version(source), default);

        Assert.Same(source, Assert.Single(_store.Removed));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task S3_нова_адреса_джерела_з_секретом_без_повторного_введення_дає_422_і_нічого_не_змінює()
    {
        // ⛔ S3 аудиту безпеки: секрет середовища прив'язаний до ІМЕНІ джерела, і
        // адаптер чіпляє його до будь-якої адреси з рядка. Без перевірки право
        // `Integration.Manage` = «надішли службовий секрет на мій хост».
        var source = Add("PI_MAIN");
        _secrets.Values["DataSource.PI_MAIN"] = SecretValue;

        // Кожен вид «нової адреси», який правило називає зміною.
        (ExternalTransport Transport, string Endpoint, string? Spare)[] moves =
        [
            (ExternalTransport.PiWebApi, "https://attacker.example/piwebapi", null),
            (ExternalTransport.PiWebApi, "https://pi.corp.example:8443/piwebapi", null),
            (ExternalTransport.PiWebApi, "http://pi.corp.example/piwebapi", null),
            (ExternalTransport.PiWebApi, "https://pi.corp.example/other", null),
            (ExternalTransport.PiWebApi, Endpoint, "https://attacker.example/piwebapi"),
            (ExternalTransport.Sql, Endpoint, null),
        ];

        foreach (var (transport, endpoint, spare) in moves)
        {
            foreach (var confirmation in new string?[] { null, string.Empty, "wrong-guess" })
            {
                var refused = await Assert.ThrowsAsync<BusinessRuleException>(
                    () => Save().UpdateAsync(
                        source.Id, Named(), transport, endpoint, spare, null, null, true, Version(source),
                        confirmation, false, default));

                Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
                Assert.Equal(SaveDataSourceHandler.SecretReentryRequiredKey, refused.Details!["messageKey"]);
                Assert.Equal("secretConfirmation", refused.Details!["field"]);
                Assert.DoesNotContain(SecretValue, refused.Message, StringComparison.Ordinal);
            }
        }

        // Рядок з'єднання: доданий `Failover Partner` веде секрет на інший сервер.
        var flert = Add("FLERT", ExternalTransport.Sql, "Server=flert;Database=Vol");
        _secrets.Values["DataSource.FLERT"] = SecretValue;

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().UpdateAsync(
                flert.Id, Named(), ExternalTransport.Sql, "Server=flert;Database=Vol;Failover Partner=evil",
                null, null, null, true, Version(flert), null, false, default));

        // Нічого не записано й не зажурнальовано: адреса та сама, що була.
        Assert.Equal(Endpoint, source.Endpoint);
        Assert.Equal(ExternalTransport.PiWebApi, source.Transport);
        Assert.Null(source.SecondaryEndpoint);
        Assert.Equal("Server=flert;Database=Vol", flert.Endpoint);
        Assert.Empty(_events);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task S3_створення_джерела_під_уже_заданий_секрет_вимагає_його_ввести()
    {
        // ⛔ Секрет, що лишився в середовищі після видаленого джерела, інакше
        // поїхав би на адресу нового джерела з тим самим кодом.
        _secrets.Values["DataSource.PI_NEW"] = SecretValue;

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().CreateAsync(
                "PI_NEW", Named(), ExternalTransport.PiWebApi, "https://attacker.example", null, null, null,
                null, default));

        Assert.Equal(SaveDataSourceHandler.SecretReentryRequiredKey, refused.Details!["messageKey"]);
        Assert.Empty(_store.Added);

        var created = await Save().CreateAsync(
            "PI_NEW", Named(), ExternalTransport.PiWebApi, Endpoint, null, null, null, SecretValue, default);

        Assert.True(created.HasSecret);

        // Контроль: без секрету в середовищі підтвердження не потрібне взагалі.
        var plain = await Save().CreateAsync(
            "PI_PLAIN", Named(), ExternalTransport.PiWebApi, Endpoint, null, null, null, null, default);

        Assert.False(plain.HasSecret);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task S3_та_сама_адреса_в_іншому_записі_не_вимагає_секрету_і_секрет_лишається()
    {
        var source = Add("PI_MAIN");
        _secrets.Values["DataSource.PI_MAIN"] = SecretValue;

        // Регістр схеми й хоста, явний типовий порт, кінцева `/` — та сама ціль.
        var saved = await Save().UpdateAsync(
            source.Id, Named(), ExternalTransport.PiWebApi, "HTTPS://PI.CORP.EXAMPLE:443/piwebapi/", null,
            "OtherDb", 8, true, Version(source), null, false, default);

        Assert.True(saved.HasSecret);
        Assert.Equal("OtherDb", saved.Catalog);

        // Рядок з'єднання: інший порядок ключів, пробіли й регістр назв — та сама ціль.
        var flert = Add("FLERT", ExternalTransport.Sql, "Server=flert;Database=Vol");
        _secrets.Values["DataSource.FLERT"] = SecretValue;

        var same = await Save().UpdateAsync(
            flert.Id, Named(), ExternalTransport.Sql, " database = Vol ; SERVER = flert ", null, null, null, true,
            Version(flert), null, false, default);

        Assert.True(same.HasSecret);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task S3_нова_адреса_з_правильним_секретом_зберігається_а_журнал_має_стару_й_нову_без_секрету()
    {
        var source = Add("PI_MAIN");
        _secrets.Values["DataSource.PI_MAIN"] = SecretValue;
        const string moved = "https://pi2.corp.example/piwebapi";

        await Save().UpdateAsync(
            source.Id, Named(), ExternalTransport.PiWebApi, moved, null, null, null, true, Version(source),
            SecretValue, false, default);

        Assert.Equal(moved, source.Endpoint);

        var recorded = Assert.Single(_events);
        Assert.Equal(SaveDataSourceHandler.EventType, recorded.EventType);

        var details = JsonDocument.Parse(recorded.DetailsJson ?? "{}").RootElement;
        Assert.Equal(Endpoint, details.GetProperty("oldEndpoint").GetString());
        Assert.Equal(moved, details.GetProperty("newEndpoint").GetString());
        Assert.True(details.GetProperty("addressChanged").GetBoolean());

        // ⛔ Ні значення секрету, ні його імені в журналі.
        Assert.DoesNotContain(SecretValue, recorded.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("DataSource.PI_MAIN", recorded.DetailsJson, StringComparison.Ordinal);

        // Без секрету в середовищі (Windows-автентифікація) адреса змінюється лише
        // з явним `confirmEndpointChange`, а журнал так само каже «звідки й куди».
        var plain = Add("PI_PLAIN");
        await Save().UpdateAsync(
            plain.Id, Named(), ExternalTransport.PiWebApi, moved, null, null, null, true, Version(plain),
            null, true, default);

        var second = JsonDocument.Parse(_events[^1].DetailsJson ?? "{}").RootElement;
        Assert.Equal(Endpoint, second.GetProperty("oldEndpoint").GetString());
        Assert.Equal(moved, second.GetProperty("newEndpoint").GetString());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    [InlineData(null)]
    [InlineData("Negotiate")]
    public async Task Negotiate_нова_адреса_без_confirmEndpointChange_дає_422_і_нічого_не_змінює(string? bound)
    {
        var source = Add("PI_NEG");

        if (bound is not null)
        {
            _secrets.Values["DataSource.PI_NEG"] = bound;
        }

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().UpdateAsync(
                source.Id, Named(), ExternalTransport.PiWebApi, "https://pi2.corp.example/piwebapi", null, null, null,
                true, Version(source), null, false, default));

        Assert.Equal("ECR-REQ-0422", refused.ErrorCode);
        Assert.Equal(SaveDataSourceHandler.EndpointChangeUnconfirmedKey, refused.Details!["messageKey"]);
        Assert.Equal("confirmEndpointChange", refused.Details!["field"]);
        Assert.Equal(Endpoint, source.Endpoint);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        // Незмінна адреса підтвердження не вимагає; з підтвердженням — зберігається.
        var same = await Save().UpdateAsync(
            source.Id, Named(), ExternalTransport.PiWebApi, Endpoint, null, "Db", null, true, Version(source),
            null, false, default);
        Assert.Equal("Db", same.Catalog);

        await Save().UpdateAsync(
            source.Id, Named(), ExternalTransport.PiWebApi, "https://pi2.corp.example/piwebapi", null, null, null,
            true, Version(source), null, true, default);
        Assert.Equal("https://pi2.corp.example/piwebapi", source.Endpoint);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "BE-21")]
    public async Task S3_проба_віддає_категорію_а_не_текст_винятку_і_текст_лише_в_серверному_журналі()
    {
        var source = Add("PI_MAIN");
        const string raw = "RAW-10.20.30.40:5432-banner";

        // ⛔ Кожен тип — окрема відповідь сканера: «refused», «timeout», «TLS»,
        // «сервер відповів». Тіло відповіді мусить бути однаковим усередині категорії.
        (Exception Error, string Category)[] cases =
        [
            (new SourceAuthenticationException("ECR-INT-0502", $"401 {raw}"), "auth"),
            (new HttpRequestException(raw, new System.Security.Authentication.AuthenticationException(raw)), "tls"),
            (new HttpRequestException(HttpRequestError.SecureConnectionError, raw), "tls"),
            (new TaskCanceledException(raw, new TimeoutException(raw)), "timeout"),
            (new BusinessRuleException(
                "ECR-INT-0503", raw, new Dictionary<string, object?> { ["reason"] = "timeout" }), "timeout"),
            (new HttpRequestException(raw, new System.Net.Sockets.SocketException(10061)), "unreachable"),
            (new BusinessRuleException("ECR-INT-0503", $"SQL-джерело не з'єднується: {raw}"), "unreachable"),
            (new InvalidOperationException(raw), "other"),
        ];

        foreach (var (error, category) in cases)
        {
            _adapter.Throw = error;

            var result = await Test().HandleAsync(source.Id, Reason, default);

            Assert.False(result.Ok);
            Assert.Equal(category, result.Error);
            Assert.Equal($"integration.test.failed.{category}", result.MessageKey);
            Assert.Contains(category, TestDataSourceConnectionHandler.FailureCategories);
            Assert.DoesNotContain(raw, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }

        // Журнал безпеки тексту теж не має — лише факт і підсумок.
        Assert.Equal(cases.Length, _events.Count);
        Assert.All(_events, e => Assert.DoesNotContain(raw, e.DetailsJson, StringComparison.Ordinal));

        // …а серверний журнал має: без нього відмову не було б чим лагодити.
        Assert.Equal(cases.Length, _log.Entries.Count(e => e.Contains(raw, StringComparison.Ordinal)));
    }

    private void Allow(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = Actor };

        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private static Dictionary<string, string> Named() => new() { ["en"] = "PI AF primary" };

    private ListDataSourcesHandler List() => new(_store, _secrets, _access, _user);

    private SaveDataSourceHandler Save() => new(_store, _secrets, _access, _uow, _audit, _user, Clock());

    private DeleteDataSourceHandler Delete() => new(_store, _access, _uow, _audit, _user, Clock());

    private TestDataSourceConnectionHandler Test()
        => new(_store, [_adapter], _gate, _access, _audit, _user, Clock(), _log);

    private static TestClock Clock() => new(new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc));

    /// <summary>Джерело з присвоєним ключем — базу тут заміняє список.</summary>
    private DataSource Add(string code, ExternalTransport transport = ExternalTransport.PiWebApi, string endpoint = Endpoint)
    {
        var source = new DataSource(
            EcrCode.Create(code), new LocalizedText(Named()),
            transport, endpoint, SaveDataSourceHandler.SecretNamePrefix + code);

        typeof(Entity<int>).GetProperty("Id")!.SetValue(source, _store.Sources.Count + 1);
        typeof(DataSource).GetProperty(nameof(DataSource.RowVersion))!
            .SetValue(source, new byte[] { 0, 0, 0, 0, 0, 0, 7, (byte)_store.Sources.Count });
        _store.Sources.Add(source);

        return source;
    }

    /// <summary>Значення <c>If-Match</c>, яке клієнт узяв би з переліку.</summary>
    private static string Version(DataSource source) => $"\"{Convert.ToBase64String(source.RowVersion)}\"";

    private sealed class FakeStore : IDataSourceStore
    {
        public List<DataSource> Sources { get; } = [];

        public List<DataSource> Added { get; } = [];

        public List<DataSource> Removed { get; } = [];

        public Dictionary<int, DataSourceUsage> Usage { get; } = [];

        public Task<IReadOnlyList<DataSourceRow>> ListAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DataSourceRow>>(
                [.. Sources.Select(s => new DataSourceRow(s, UsageOf(s.Id)))]);

        public Task<DataSource?> FindAsync(int dataSourceId, CancellationToken ct)
            => Task.FromResult(Sources.Find(s => s.Id == dataSourceId));

        public Task<bool> IsCodeTakenAsync(string code, int? exceptId, CancellationToken ct)
            => Task.FromResult(Sources.Exists(
                s => string.Equals(s.Code, code, StringComparison.Ordinal) && s.Id != exceptId));

        public Task<DataSourceUsage> CountUsageAsync(int dataSourceId, CancellationToken ct)
            => Task.FromResult(UsageOf(dataSourceId));

        public void Add(DataSource source)
        {
            typeof(Entity<int>).GetProperty("Id")!.SetValue(source, Sources.Count + 1);
            Sources.Add(source);
            Added.Add(source);
        }

        public void Remove(DataSource source)
        {
            Removed.Add(source);
            Sources.RemoveAll(s => s.Id == source.Id);
        }

        private DataSourceUsage UsageOf(int id)
            => Usage.TryGetValue(id, out var usage) ? usage : new DataSourceUsage(0, 0);
    }

    /// <summary>Серверний журнал: повідомлення разом із текстом винятку.</summary>
    private sealed class CapturingLogger : ILogger<TestDataSourceConnectionHandler>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add($"{formatter(state, exception)} {exception?.Message}");
    }

    /// <summary>Середовище, яке може дати секрет під ім'я.</summary>
    private sealed class FakeSecrets : ISecretProvider
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? Find(string secretName)
            => Values.TryGetValue(secretName, out var value) ? value : null;
    }

    /// <summary>
    /// Подвійник адаптера: каталог із однієї позиції і керована пауза всередині.
    /// </summary>
    /// <remarks>
    /// ⛔ Жодної мережі. Проба ходить ТИМ САМИМ інтерфейсом, яким збирають, тож
    /// підміна на цьому рівні перевіряє справжній шлях обробника.
    /// </remarks>
    private sealed class FakeAdapter : IExternalDataSource
    {
        public TaskCompletionSource Entered { get; } = new();

        public TaskCompletionSource Release { get; } = new();

        /// <summary>Відмова, якою відповідає «джерело»; <c>null</c> — відповідає каталогом.</summary>
        public Exception? Throw { get; set; }

        public ExternalTransport Transport => ExternalTransport.PiWebApi;

        public async Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct)
        {
            Entered.TrySetResult();

            if (Throw is { } error)
            {
                throw error;
            }

            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

            return [new SourceEntityDescriptor("Unit-01", "Probe element", @"\Srv\Db\Unit-01", null, "Element")];
        }

        public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct)
            => throw new NotSupportedException("Проба каталогу значень не читає.");
    }
}
