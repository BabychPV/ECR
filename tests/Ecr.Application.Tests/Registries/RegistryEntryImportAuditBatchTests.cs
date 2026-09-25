// tests/Ecr.Application.Tests/Registries/RegistryEntryImportAuditBatchTests.cs
using System.Text;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
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
/// Імпорт CSV довідника пише пер-рядковий слід <c>aud.SecurityEvent</c> ОДНИМ
/// пакетним викликом <see cref="IAuditWriter.WriteSecurityEventsAsync"/>, а не
/// циклом поштучних <see cref="IAuditWriter.WriteSecurityEventAsync"/>.
/// </summary>
/// <remarks>
/// ⛔ Той самий клас проблеми, що <c>B-10</c>
/// (<c>RegistryEntryImportQueryCountTests</c> у <c>Ecr.Api.Tests</c>)
/// уже закрив для ЧИТАННЯ під час цього імпорту — тут те саме для ЗАПИСУ
/// журналу аудиту: N змінених записів довідника раніше давали N окремих
/// <c>INSERT</c> до <c>aud.SecurityEvent</c> (по одному <c>await</c> на
/// запис), тепер — один похід до сховища на весь файл.
/// <para>
/// ⛔ Мутація, що валить тест: повернути в
/// <c>ImportRegistryEntriesHandler.HandleAsync</c> цикл
/// <c>foreach (var (entry, changes) in valueChanges) await audit.WriteSecurityEventAsync(...)</c>
/// замість одного <c>WriteSecurityEventsAsync</c> — перше твердження
/// (<c>Received(1)</c>/<c>DidNotReceive</c>) стає червоним. Підмінити пакетний
/// виклик одним записом, повтореним N разів, — впаде перевірка різних
/// <c>entryId</c> у зібраному списку.
/// </para>
/// </remarks>
public sealed class RegistryEntryImportAuditBatchTests
{
    private const int RegistryDefId = 11;
    private const int NameFieldId = 501;

    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    public RegistryEntryImportAuditBatchTests()
    {
        // ⛔ Q-244: обробник виконує аудит/SaveChanges через
        // IUnitOfWork.ExecuteInTransactionAsync(Func<CancellationToken, Task>, ...).
        // Без цього налаштування NSubstitute ніколи не викликає передане
        // замикання — перевірки нижче не виконались би насправді.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _user.Language.Returns("uk");
        _user.CorrelationId.Returns("corr-import");

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(UpsertRegistryEntryHandler.Permission).Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Кілька_змінених_записів_дають_один_пакетний_виклик_а_не_цикл()
    {
        var definition = Definition();
        var nameField = Field(definition.Id, "NAME", NameFieldId);
        definition.AddField(nameField);

        // ⚠ Записи ІСНУЮЧІ (не нові): Id призначає лише реальна база — для
        // нових записів у цьому тесті (SaveChangesAsync — заглушка) Id лишився
        // б 0 для всіх, і перевірка різних entryId нижче нічого не довела б.
        var entries = Enumerable.Range(0, 4)
            .Select(i => Entry(100 + i, definition.Id, $"E{i}"))
            .ToList();

        var values = entries
            .Select(e =>
            {
                var value = new RegistryValue(e.Id, nameField.Id);
                value.Set(CellDataType.String, $"old{e.Id}", null);
                return value;
            })
            .ToList();

        _registries.FindDefinitionAsync("PERMITS", Arg.Any<CancellationToken>()).Returns(definition);
        _registries
            .FindEntriesByCodesAsync(definition.Id, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryEntry>)entries);
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)values);

        List<SecurityEventRecord>? captured = null;
        _audit
            .WriteSecurityEventsAsync(Arg.Do<IReadOnlyList<SecurityEventRecord>>(r => captured = [.. r]), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var handler = new ImportRegistryEntriesHandler(_registries, _uow, _audit, _access, _user, _clock);

        var csv = new StringBuilder("code,NAME\r\n");
        for (var i = 0; i < entries.Count; i++)
        {
            csv.Append(entries[i].Code).Append(",new").Append(i).Append("\r\n");
        }

        var content = csv.ToString();
        var report = await handler
            .HandleAsync(
                "PERMITS", content, Encoding.UTF8.GetByteCount(content), ImportRegistryEntriesHandler.DefaultMaxBytes,
                dryRun: false, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Empty(report.Errors);
        Assert.True(report.Applied);
        Assert.Equal(4, report.Updated);

        // ⛔ Головне твердження: ОДИН пакетний виклик на весь файл, а не N
        // поштучних — саме той N+1, який мала закрити ця правка.
        await _audit.Received(1)
            .WriteSecurityEventsAsync(Arg.Any<IReadOnlyList<SecurityEventRecord>>(), Arg.Any<CancellationToken>())
            .ConfigureAwait(true);
        await _audit.DidNotReceive()
            .WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>())
            .ConfigureAwait(true);

        Assert.NotNull(captured);
        Assert.Equal(4, captured!.Count);

        // ⛔ Не просто «один виклик»: кожен рядок пакета мусить нести СВІЙ
        // entryId, а не той самий запис, повторений N разів (мутація, яку
        // Received(1) сам по собі не ловить).
        var entryIds = captured
            .Select(r => JsonDocument.Parse(r.DetailsJson!).RootElement.GetProperty("entryId").GetInt64())
            .OrderBy(id => id)
            .ToList();
        Assert.Equal(entries.Select(e => e.Id).OrderBy(id => id), entryIds);

        Assert.All(captured, r =>
        {
            Assert.Equal(UpsertRegistryEntryHandler.ValueChangedEventType, r.EventType);
            Assert.Equal(9, r.ChangedByUserId);
            Assert.Equal("corr-import", r.CorrelationId);
        });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_фактичних_змін_пакетний_метод_не_викликається()
    {
        var definition = Definition();
        var nameField = Field(definition.Id, "NAME", NameFieldId);
        definition.AddField(nameField);

        var entry = Entry(200, definition.Id, "E0");
        var value = new RegistryValue(entry.Id, nameField.Id);
        value.Set(CellDataType.String, "same", null);

        _registries.FindDefinitionAsync("PERMITS", Arg.Any<CancellationToken>()).Returns(definition);
        _registries
            .FindEntriesByCodesAsync(definition.Id, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryEntry>)[entry]);
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[value]);

        var handler = new ImportRegistryEntriesHandler(_registries, _uow, _audit, _access, _user, _clock);

        // ⚠ Те саме значення, що вже лежить у value — жодної фактичної зміни.
        var content = $"code,NAME\r\n{entry.Code},same\r\n";
        var report = await handler
            .HandleAsync(
                "PERMITS", content, Encoding.UTF8.GetByteCount(content), ImportRegistryEntriesHandler.DefaultMaxBytes,
                dryRun: false, CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Empty(report.Errors);

        await _audit.DidNotReceive()
            .WriteSecurityEventsAsync(Arg.Any<IReadOnlyList<SecurityEventRecord>>(), Arg.Any<CancellationToken>())
            .ConfigureAwait(true);
        await _audit.DidNotReceive()
            .WriteSecurityEventAsync(Arg.Any<SecurityEventRecord>(), Arg.Any<CancellationToken>())
            .ConfigureAwait(true);
    }

    /// <summary>Опис довідника з призначеним <c>Id</c> — його дає база.</summary>
    private static RegistryDef Definition()
    {
        var definition = new RegistryDef(EcrCode.Create("PERMITS"), Text("Permits"), isTemporal: false);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, RegistryDefId);
        return definition;
    }

    /// <summary>Поле довідника з призначеним <c>Id</c> — його дає база.</summary>
    private static RegistryFieldDef Field(int registryDefId, string code, int id)
    {
        var field = new RegistryFieldDef(registryDefId, EcrCode.Create(code), Text(code), CellDataType.String, ordinal: 1);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(field, id);
        return field;
    }

    /// <summary>Запис довідника з призначеним <c>Id</c> — його дає база.</summary>
    private static RegistryEntry Entry(long id, int registryDefId, string code)
    {
        var entry = new RegistryEntry(registryDefId, EcrCode.Create(code), Text(code));
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(entry, id);
        return entry;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
