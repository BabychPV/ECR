// tests/Ecr.Application.Tests/Registries/RegistryEntryWriterAuditFixesTests.cs
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
/// Аудит 2026-10-03, розділ 0E (AN-29): запис у видалений запис за кодом (L5-01), очищення
/// обов'язкового поля (L4-06 = L5-03) і рядок довший за колонку (L4-03 = L5-10) — через спільний
/// <see cref="RegistryEntryWriter"/> (пакет, синк) та імпорт CSV.
/// </summary>
/// <remarks>
/// Мутаційні докази (2026-10-03, локальний worktree, не запушено):
/// <list type="bullet">
/// <item>у <c>WriteTargetsAsync</c> прибрати перевірку <c>target.Existing is { IsDeleted: true }</c> →
/// <see cref="Новий_рядок_з_кодом_видаленого_запису_це_помилка_entryCodeTaken"/> червоний
/// (рядок «оновив» видалений запис, applied=true);</item>
/// <item>у <c>ImportRegistryEntriesHandler</c> прибрати ту саму перевірку <c>byCode</c> →
/// <see cref="CSV_рядок_з_кодом_видаленого_запису_це_помилка_entryCodeTaken"/> червоний;</item>
/// <item>у <c>ApplyValuesAsync</c> повернути умову <c>!existing.ContainsKey(f.Id)</c> →
/// <see cref="Очищення_обов_язкового_поля_наявного_запису_відхиляється"/> червоний; прибрати
/// <c>IsBlank</c> (лишити <c>is null</c>) → <see cref="Пробільний_рядок_в_обов_язковому_полі_відхиляється"/> червоний;</item>
/// <item>у <c>RegistryValue.Set</c> прибрати перевірку довжини → обидва тести «довший за 1000» червоні.</item>
/// </list>
/// </remarks>
public sealed class RegistryEntryWriterAuditFixesTests
{
    private const int RegistryId = 14;
    private const int StreamId = 141;
    private const int CaseId = 142;
    private const int UserId = 9;
    private static readonly DateTime Now = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryKeyStore _keyStore = Substitute.For<IRegistryKeyStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly List<RegistryEntry> _addedEntries = [];
    private readonly Dictionary<RegistryEntry, List<RegistryValue>> _values = [];
    private readonly RegistryDef _definition;

    public RegistryEntryWriterAuditFixesTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(UserId);
        _user.CorrelationId.Returns("corr-an29");

        _definition = new RegistryDef(EcrCode.Create("STREAMS"), Text("Streams"), isTemporal: false);
        SetId(_definition, RegistryId);
        _definition.AddField(Field(StreamId, "STREAM", required: true));
        _definition.AddField(Field(CaseId, "CASE_NAME", required: false));

        _registries.FindDefinitionByIdAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(_definition);
        _registries.FindDefinitionAsync("STREAMS", Arg.Any<CancellationToken>()).Returns(_definition);
        _registries
            .FindEntriesByCodesAsync(RegistryId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryEntry>());
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryValue>());
        _registries.When(r => r.Add(Arg.Any<RegistryEntry>())).Do(c => _addedEntries.Add(c.Arg<RegistryEntry>()));

        _keyStore.ListActiveKeysAsync(RegistryId, Arg.Any<CancellationToken>()).Returns(Array.Empty<RegistryKeyDef>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.6")]
    public async Task Новий_рядок_з_кодом_видаленого_запису_це_помилка_entryCodeTaken()
    {
        var gone = Existing(301L, "E1", ("1D-2", null));
        gone.SoftDelete(UserId, Now);

        var result = await Writer().WriteAsync(
            Batch(new RegistryEntryWrite("E1", new Dictionary<string, object?> { ["STREAM"] = "1D-9" })),
            CancellationToken.None);

        Assert.False(result.Applied);
        var error = Assert.Single(result.Errors);
        Assert.Equal((1, "E1", RegistryEntryWriter.EntryCodeTakenKey), (error.Row, error.Key, error.MessageKey));
        Assert.Equal("301", error.Params!["id"]);
        Assert.Equal((0, 0, 0), (result.Added, result.Updated, result.Unchanged));
        Assert.Empty(_addedEntries);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.6")]
    public async Task CSV_рядок_з_кодом_видаленого_запису_це_помилка_entryCodeTaken()
    {
        var gone = Existing(302L, "E1", ("1D-2", null));
        gone.SoftDelete(UserId, Now);

        const string csv = "code,STREAM,CASE_NAME\r\nE1,1D-9,Winter\r\n";
        var report = await CsvHandler().HandleAsync(
            "STREAMS", csv, csv.Length, ImportRegistryEntriesHandler.DefaultMaxBytes, dryRun: false, CancellationToken.None);

        Assert.False(report.Applied);
        var error = Assert.Single(report.Errors);
        Assert.Equal((2, "E1", RegistryEntryWriter.EntryCodeTakenKey), (error.Row, error.Key, error.MessageKey));
        Assert.Equal("302", error.Params!["id"]);
        Assert.Equal("1D-2", _values[gone].Single(v => v.RegistryFieldDefId == StreamId).ValueString);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.4")]
    public async Task Очищення_обов_язкового_поля_наявного_запису_відхиляється()
    {
        Existing(303L, "E1", ("1D-2", "Winter"));

        var result = await Writer().WriteAsync(
            Batch(new RegistryEntryWrite("E1", new Dictionary<string, object?> { ["STREAM"] = null })),
            CancellationToken.None);

        await AssertRequiredMissingAsync(result);
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.4")]
    public async Task Пробільний_рядок_в_обов_язковому_полі_відхиляється()
    {
        Existing(304L, "E1", ("1D-2", "Winter"));

        var result = await Writer().WriteAsync(
            Batch(new RegistryEntryWrite("E1", new Dictionary<string, object?> { ["STREAM"] = "   " })),
            CancellationToken.None);

        await AssertRequiredMissingAsync(result);
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.4")]
    public async Task Синк_очищення_обов_язкового_поля_за_Id_це_помилка_рядка()
    {
        var entry = Existing(305L, "E1", ("1D-2", "Winter"));
        _registries.FindEntryAsync(305L, Arg.Any<CancellationToken>()).Returns(entry);

        var result = await Writer().UpdateAsync(
            new RegistryEntryUpdateBatch(
                RegistryId, [new RegistryEntryUpdate(305L, new Dictionary<string, object?> { ["STREAM"] = null })]),
            CancellationToken.None);

        await AssertRequiredMissingAsync(result);
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.4")]
    public async Task Очищення_необов_язкового_поля_дозволене()
    {
        Existing(306L, "E1", ("1D-2", "Winter"));

        var result = await Writer().WriteAsync(
            Batch(new RegistryEntryWrite("E1", new Dictionary<string, object?> { ["CASE_NAME"] = null })),
            CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Empty(result.Errors);
        Assert.Equal((0, 1, 0), (result.Added, result.Updated, result.Unchanged));
    }

    [Fact]
    public async Task Рядок_довший_за_1000_символів_це_помилка_рядка_з_полем()
    {
        var result = await Writer().WriteAsync(
            Batch(new RegistryEntryWrite("E1", new Dictionary<string, object?>
            {
                ["STREAM"] = "1D-2",
                ["CASE_NAME"] = new string('x', RegistryValue.MaxStringLength + 1),
            })),
            CancellationToken.None);

        Assert.False(result.Applied);
        var error = Assert.Single(result.Errors);
        Assert.Equal((1, "E1", "CASE_NAME", "err.ECR-REG-0422.valueTooLong"), (error.Row, error.Key, error.Field, error.MessageKey));
        Assert.Equal("1000", error.Params!["max"]);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CSV_задовге_значення_дає_помилку_рядка_з_полем()
    {
        var csv = $"code,STREAM,CASE_NAME\r\nE1,1D-2,{new string('x', RegistryValue.MaxStringLength + 1)}\r\n";
        var report = await CsvHandler().HandleAsync(
            "STREAMS", csv, csv.Length, ImportRegistryEntriesHandler.DefaultMaxBytes, dryRun: false, CancellationToken.None);

        Assert.False(report.Applied);
        var error = Assert.Single(report.Errors);
        Assert.Equal((2, "CASE_NAME", "err.ECR-REG-0422.valueTooLong"), (error.Row, error.Field, error.MessageKey));
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private async Task AssertRequiredMissingAsync(RegistryEntryWriteResult result)
    {
        Assert.False(result.Applied);
        var error = Assert.Single(result.Errors);
        Assert.Equal((1, "E1", "STREAM", "err.ECR-REG-0422.requiredFieldsMissing"), (error.Row, error.Key, error.Field, error.MessageKey));
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteSecurityEventsAsync(Arg.Any<IReadOnlyList<SecurityEventRecord>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Наявний запис із збереженими значеннями, видимий writer'у й імпорту за кодом.</summary>
    private RegistryEntry Existing(long id, string code, (string Stream, string? Case) stored)
    {
        var entry = new RegistryEntry(RegistryId, EcrCode.Create(code), Text(code), 1, Now);
        SetId(entry, id);
        var values = new List<RegistryValue> { Stored(id, StreamId, stored.Stream) };
        if (stored.Case is not null)
        {
            values.Add(Stored(id, CaseId, stored.Case));
        }

        _values[entry] = values;
        _registries
            .FindEntriesByCodesAsync(RegistryId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { entry });
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(values);
        _registries.ListValuesAsync(id, Arg.Any<CancellationToken>()).Returns(values);
        return entry;
    }

    private RegistryEntryWriter Writer()
        => new(_registries, _uow, _audit, _user, _clock, new RegistryKeyService(_keyStore, _uow));

    private ImportRegistryEntriesHandler CsvHandler()
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }.Permission(UpsertRegistryEntryHandler.Permission).Build());
        return new ImportRegistryEntriesHandler(_registries, _audit, access, _user, _clock, Writer());
    }

    private static RegistryEntryWriteBatch Batch(params RegistryEntryWrite[] entries) => new(RegistryId, entries);

    private static RegistryValue Stored(long entryId, int fieldId, string text)
    {
        var value = new RegistryValue(entryId, fieldId);
        value.Set(CellDataType.String, text, unitId: null);
        return value;
    }

    private static RegistryFieldDef Field(int id, string code, bool required)
    {
        var field = new RegistryFieldDef(RegistryId, EcrCode.Create(code), Text(code), CellDataType.String, id);
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
