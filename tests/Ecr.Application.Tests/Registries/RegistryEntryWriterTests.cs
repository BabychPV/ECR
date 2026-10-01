// tests/Ecr.Application.Tests/Registries/RegistryEntryWriterTests.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Keys;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Пакетний запис довідника через спільний <see cref="RegistryEntryWriter"/> (S6) — API для
/// неінтерактивних викликів (фонова задача запису значень PI в поля довідника, ФВ-8.11).
/// </summary>
/// <remarks>
/// Мутаційні докази: у <c>SaveBatchAsync</c> прибрати виклик <c>keys.ApplyAsync</c> →
/// <see cref="Пакет_із_двох_записів_пише_обидва_з_ключами_й_автором"/> і
/// <see cref="Імпорт_CSV_пише_ключі_через_writer"/> червоні (рядків ключа 0 замість 2); прибрати в <c>WriteAsync</c> умову <c>errors.Count &gt; 0</c> →
/// <see cref="Помилка_в_одному_рядку_пакета_не_записує_нічого"/> червоний (збереження відбулося).
/// </remarks>
public sealed class RegistryEntryWriterTests
{
    private const int RegistryId = 12;
    private const int StreamId = 121;
    private const int CaseId = 122;
    private const int LimitId = 123;
    private const int PrimaryKeyId = 1200;
    private const int UserId = 9;
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] BatchCodes = ["E1", "E2"];
    private static readonly string[] KeyFieldCodes = ["STREAM", "CASE_NAME"];

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryKeyStore _keyStore = Substitute.For<IRegistryKeyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly List<RegistryEntry> _addedEntries = [];
    private readonly List<RegistryValue> _addedValues = [];
    private readonly RegistryDef _definition;

    public RegistryEntryWriterTests()
    {
        // Транзакція виконує передане замикання — інакше перевірки нижче нічого б не довели.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(UserId);
        _user.CorrelationId.Returns("corr-writer");

        (_definition, var key) = Registry();
        _registries.FindDefinitionByIdAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(_definition);
        _registries
            .FindEntriesByCodesAsync(RegistryId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryEntry>());
        _registries.When(r => r.Add(Arg.Any<RegistryEntry>())).Do(c => _addedEntries.Add(c.Arg<RegistryEntry>()));
        _registries.When(r => r.AddValue(Arg.Any<RegistryValue>())).Do(c => _addedValues.Add(c.Arg<RegistryValue>()));

        _keyStore.ListActiveKeysAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(new[] { key });
        _keyStore.ListEntryKeysAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<RegistryEntryKey>());
        _keyStore.FindLiveHoldersForUpdateAsync(Arg.Any<int>(), Arg.Any<byte[]>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryKeyHolder>());
        _keyStore.ListCurrentValuesAsync(Arg.Any<RegistryEntry>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<RegistryValue>)[.. _addedValues.Where(v => ReferenceEquals(v.Entry, call.Arg<RegistryEntry>()))]);
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Пакет_із_двох_записів_пише_обидва_з_ключами_й_автором()
    {
        List<SecurityEventRecord>? events = null;
        _audit
            .WriteSecurityEventsAsync(Arg.Do<IReadOnlyList<SecurityEventRecord>>(r => events = [.. r]), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var revision = _definition.DataRevision;

        var result = await Writer().WriteAsync(
            Batch(Row("E1", "1D-2", "Winter"), Row("E2", "1D-2", "Summer")), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Empty(result.Errors);
        Assert.Equal((2, 0, 0), (result.Added, result.Updated, result.Unchanged));

        // Обидва записи — нові, з автором; обидва значення кожного — у сховищі.
        Assert.Equal(BatchCodes, _addedEntries.Select(e => e.Code));
        Assert.All(_addedEntries, e => Assert.Equal(UserId, e.CreatedByUserId));
        Assert.Equal(4, _addedValues.Count);

        // Ключ — рядок на кожен запис, у транзакції збереження.
        _keyStore.Received(2).Add(Arg.Any<RegistryEntryKey>());
        await _uow.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(revision + 1, _definition.DataRevision);

        // Аудит — одним пакетним викликом, подія на запис, автор і кореляція виклику.
        await _audit.DidNotReceive().WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>());
        Assert.NotNull(events);
        Assert.Equal(2, events!.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(RegistryEntryWriter.ValueChangedEventType, e.EventType);
            Assert.Equal(UserId, e.ChangedByUserId);
            Assert.Equal("corr-writer", e.CorrelationId);
            var fields = JsonDocument.Parse(e.DetailsJson!).RootElement.GetProperty("changes")
                .EnumerateArray().Select(c => c.GetProperty("field").GetString()).ToList();
            Assert.Equal(KeyFieldCodes, fields);
        });
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Помилка_в_одному_рядку_пакета_не_записує_нічого()
    {
        var revision = _definition.DataRevision;

        var result = await Writer().WriteAsync(
            Batch(
                Row("E1", "1D-2", "Winter"),
                Row("E2", "1D-2", "Summer", limit: "not a number")),
            CancellationToken.None);

        Assert.False(result.Applied);
        var error = Assert.Single(result.Errors);
        Assert.Equal((2, "E2", "LIMIT", "err.ECR-REG-0422.valueNotNumber"), (error.Row, error.Key, error.Field, error.MessageKey));
        Assert.Equal("not a number", error.Params!["value"]);

        await _uow.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _keyStore.DidNotReceive().Add(Arg.Any<RegistryEntryKey>());
        await _audit.DidNotReceive().WriteSecurityEventsAsync(Arg.Any<IReadOnlyList<SecurityEventRecord>>(), Arg.Any<CancellationToken>());
        Assert.Equal(revision, _definition.DataRevision);
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Результат_містить_помилки_кожного_рядка_з_полем()
    {
        var result = await Writer().WriteAsync(
            Batch(
                new RegistryEntryWrite("E1", new Dictionary<string, object?> { ["STREAM"] = "1D-2" }),
                Row("E2", "1D-2", "Summer"),
                new RegistryEntryWrite("E3", new Dictionary<string, object?> { ["STREAM"] = "1D-3", ["CASE_NAME"] = "A", ["NOPE"] = 1 })),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(
            new[]
            {
                new RegistryEntryImportError(1, "E1", "CASE_NAME", "err.ECR-REG-0422.requiredFieldsMissing"),
                new RegistryEntryImportError(3, "E3", "NOPE", "err.ECR-REG-0422.unknownFields"),
            },
            result.Errors);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Пакет_без_фактичних_змін_не_зберігає_й_не_рухає_ревізію()
    {
        var entry = new RegistryEntry(RegistryId, EcrCode.Create("E1"), Text("E1"), 1, Now);
        SetId(entry, 77L);
        _registries
            .FindEntriesByCodesAsync(RegistryId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { entry });
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Stored(77L, StreamId, "1D-2"), Stored(77L, CaseId, "Winter") });
        var revision = _definition.DataRevision;

        var result = await Writer().WriteAsync(Batch(Row("E1", "1D-2", "Winter")), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Empty(result.Errors);
        Assert.Equal((0, 0, 1), (result.Added, result.Updated, result.Unchanged));
        Assert.Empty(_addedEntries);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.Equal(revision, _definition.DataRevision);
    }

    /// <summary>
    /// Імпорт CSV пише ключі тим самим writer'ом, що й пакет: HTTP-тести RT-10b перевіряють
    /// дубль у файлі й пошук за ключем, але не те, що імпорт узагалі створює рядок ключа.
    /// </summary>
    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task Імпорт_CSV_пише_ключі_через_writer()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }.Permission(UpsertRegistryEntryHandler.Permission).Build());
        _registries.FindDefinitionAsync("STREAM_CASE", Arg.Any<CancellationToken>()).Returns(_definition);
        _keyStore.FindLiveHoldersAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<byte[]>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryKeyHolder>());

        const string csv = "code,STREAM,CASE_NAME\r\nE1,1D-2,Winter\r\nE2,1D-2,Summer\r\n";
        var report = await new ImportRegistryEntriesHandler(_registries, _audit, access, _user, _clock, Writer())
            .HandleAsync("STREAM_CASE", csv, csv.Length, ImportRegistryEntriesHandler.DefaultMaxBytes, dryRun: false, CancellationToken.None);

        Assert.True(report.Applied);
        Assert.Equal(2, report.Added);
        _keyStore.Received(2).Add(Arg.Any<RegistryEntryKey>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// S7-3 (FEATURE-REGISTRY-TABLES §4.3 крок 3): два записи пакета отримують той самий ключ —
    /// помилка ОБОХ рядків, нічого не зберігається. Служба ключів тримачів із пакета не
    /// перевіряє, тож без цього кроку дубль доходив до індексу бази.
    /// </summary>
    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task UpdateAsync_дубль_ключа_в_пакеті_це_помилка_обох_рядків_без_збереження()
    {
        var e1 = new RegistryEntry(RegistryId, EcrCode.Create("E1"), Text("E1"), 1, Now);
        SetId(e1, 77L);
        var e2 = new RegistryEntry(RegistryId, EcrCode.Create("E2"), Text("E2"), 1, Now);
        SetId(e2, 78L);
        _registries.FindEntryAsync(77L, Arg.Any<CancellationToken>()).Returns(e1);
        _registries.FindEntryAsync(78L, Arg.Any<CancellationToken>()).Returns(e2);
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Stored(77L, StreamId, "1D-2"), Stored(77L, CaseId, "Winter"),
                Stored(78L, StreamId, "1D-2"), Stored(78L, CaseId, "Summer"),
            });
        var revision = _definition.DataRevision;

        // Обидва — на «Autumn» (ключ без урахування регістру): кожен окремо вільний, разом — дубль.
        var result = await Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(
                RegistryId,
                [
                    new RegistryEntryUpdate(77L, new Dictionary<string, object?> { ["CASE_NAME"] = "Autumn" }),
                    new RegistryEntryUpdate(78L, new Dictionary<string, object?> { ["CASE_NAME"] = "AUTUMN" }),
                ]),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(
            new[]
            {
                new RegistryEntryImportError(1, "E1", "STREAM, CASE_NAME", RegistryEntryWriter.KeyDuplicateInBatchKey),
                new RegistryEntryImportError(2, "E2", "STREAM, CASE_NAME", RegistryEntryWriter.KeyDuplicateInBatchKey),
            },
            result.Errors);
        Assert.Equal((0, 0, 0), (result.Added, result.Updated, result.Unchanged));

        await _uow.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _keyStore.DidNotReceive().Add(Arg.Any<RegistryEntryKey>());
        await _audit.DidNotReceive().WriteSecurityEventsAsync(Arg.Any<IReadOnlyList<SecurityEventRecord>>(), Arg.Any<CancellationToken>());
        Assert.Equal(revision, _definition.DataRevision);
    }

    /// <summary>
    /// S7-3: те саме для <see cref="RegistryEntryWriter.WriteAsync"/> — прогалина S6 (пакет нових
    /// записів із тим самим ключем писався, бо служба ключів звіряє лише з базою).
    /// </summary>
    [Fact]
    [Trait("Requirement", "ФВ-8.15")]
    public async Task WriteAsync_дубль_ключа_в_пакеті_це_помилка_обох_рядків_без_збереження()
    {
        var result = await Writer().WriteAsync(
            Batch(Row("E1", "1D-2", "Winter"), Row("E2", "1d-2", "winter"), Row("E3", "1D-2", "Summer")),
            CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(
            new[]
            {
                new RegistryEntryImportError(1, "E1", "STREAM, CASE_NAME", RegistryEntryWriter.KeyDuplicateInBatchKey),
                new RegistryEntryImportError(2, "E2", "STREAM, CASE_NAME", RegistryEntryWriter.KeyDuplicateInBatchKey),
            },
            result.Errors);

        // E3 з іншим ключем пройшов би — його й лічить результат.
        Assert.Equal((1, 0, 0), (result.Added, result.Updated, result.Unchanged));
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _keyStore.DidNotReceive().Add(Arg.Any<RegistryEntryKey>());
    }

    [Fact]
    public async Task Повторений_у_пакеті_код_це_помилка_виклику_а_не_даних()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Writer().WriteAsync(
            Batch(Row("E1", "1D-2", "Winter"), Row("e1", "1D-2", "Summer")), CancellationToken.None));

        _registries.DidNotReceive().Add(Arg.Any<RegistryEntry>());
    }

    private RegistryEntryWriter Writer()
        => new(_registries, _uow, _audit, _user, _clock, new RegistryKeyService(_keyStore, _uow));

    private static RegistryEntryWriteBatch Batch(params RegistryEntryWrite[] entries) => new(RegistryId, entries);

    private static RegistryEntryWrite Row(string code, string stream, string caseName, object? limit = null)
    {
        var values = new Dictionary<string, object?> { ["STREAM"] = stream, ["CASE_NAME"] = caseName };
        if (limit is not null)
        {
            values["LIMIT"] = limit;
        }

        return new RegistryEntryWrite(code, values);
    }

    private static RegistryValue Stored(long entryId, int fieldId, string text)
    {
        var value = new RegistryValue(entryId, fieldId);
        value.Set(CellDataType.String, text, unitId: null);
        return value;
    }

    private static (RegistryDef Definition, RegistryKeyDef Key) Registry()
    {
        var definition = new RegistryDef(EcrCode.Create("STREAM_CASE"), Text("Stream cases"), isTemporal: false);
        SetId(definition, RegistryId);

        var stream = Field(StreamId, "STREAM", CellDataType.String, required: true);
        var caseName = Field(CaseId, "CASE_NAME", CellDataType.String, required: true);
        definition.AddField(stream);
        definition.AddField(caseName);
        definition.AddField(Field(LimitId, "LIMIT", CellDataType.Int, required: false));

        var key = new RegistryKeyDef(RegistryId, EcrCode.Create("PK"), Text("PK"), [stream, caseName], isPrimary: true, ignoreCase: true, 0, Now);
        SetId(key, PrimaryKeyId);
        return (definition, key);
    }

    private static RegistryFieldDef Field(int id, string code, CellDataType dataType, bool required)
    {
        var field = new RegistryFieldDef(RegistryId, EcrCode.Create(code), Text(code), dataType, id);
        field.Update(Text(code), id, isRequired: required);
        SetId(field, id);
        return field;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static void SetId(RegistryEntry entity, long id)
        => typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entity, id);
}
