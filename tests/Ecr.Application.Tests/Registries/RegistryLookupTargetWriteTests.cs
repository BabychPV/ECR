// tests/Ecr.Application.Tests/Registries/RegistryLookupTargetWriteTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
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
/// Запис значення поля <c>Lookup</c> (ent6 R1, R2): ціль поля обов'язкова, а відмова «запис іншого
/// довідника» не віддає код чужого запису.
/// </summary>
/// <remarks>
/// Мутаційні докази: без перевірки <c>RefRegistryDefId is null</c> у <c>RequireLookupTargetAsync</c>
/// червоний <see cref="Поле_Lookup_без_цілі_не_приймає_запис_жодного_довідника"/>; якщо повернути
/// <c>entryCode</c> у деталі — <see cref="Запис_чужого_довідника_відхиляється_без_коду_чужого_запису"/>.
/// </remarks>
public sealed class RegistryLookupTargetWriteTests
{
    private const int OwnerId = 41;
    private const int TargetId = 42;
    private const int ForbiddenId = 99;
    private const int LinkFieldId = 411;
    private const long ForeignEntryId = 9001L;
    private const string ForeignCode = "SECRET_ENTRY_CODE";
    private static readonly DateTime Now = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly RegistryDef _owner;
    private readonly RegistryFieldDef _link;

    public RegistryLookupTargetWriteTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(7);
        _user.CorrelationId.Returns("corr-lookup");

        _owner = new RegistryDef(EcrCode.Create("OWNER"), Text("OWNER"), isTemporal: false);
        SetId(_owner, OwnerId);
        _link = new RegistryFieldDef(OwnerId, EcrCode.Create("LINK"), Text("LINK"), CellDataType.Lookup, 1);
        SetId(_link, LinkFieldId);
        _owner.AddField(_link);

        _registries.FindDefinitionByIdAsync(OwnerId, Arg.Any<CancellationToken>()).Returns(_owner);
        _registries.FindDefinitionByIdAsync(TargetId, Arg.Any<CancellationToken>())
            .Returns(new RegistryDef(EcrCode.Create("TARGET"), Text("TARGET"), false));
        _registries
            .FindEntriesByCodesAsync(OwnerId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryEntry>());
        _registries
            .ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RegistryValue>());

        // Запис ЧУЖОГО (заборонено читати) довідника: існує, живий.
        var foreign = new RegistryEntry(ForbiddenId, EcrCode.Create(ForeignCode), Text(ForeignCode), 1, Now);
        typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(foreign, ForeignEntryId);
        _registries.FindEntryAsync(ForeignEntryId, Arg.Any<CancellationToken>()).Returns(foreign);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Поле_Lookup_без_цілі_не_приймає_запис_жодного_довідника()
    {
        // Старий стан: ціль null → перевірка довідника мовчала, запис з ЧУЖОГО довідника проходив.
        var result = await Write();

        Assert.False(result.Applied);
        var error = Assert.Single(result.Errors);
        Assert.Equal("err.ECR-REG-0422.lookupTargetUnknown", error.MessageKey);

        // Відмова ПЕРШОЮ, до читання запису: інакше «є/немає» стало б оракулом існування id.
        await _registries.DidNotReceive().FindEntryAsync(ForeignEntryId, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Запис_чужого_довідника_відхиляється_без_коду_чужого_запису()
    {
        _link.PointTo(TargetId);

        var result = await Write();

        Assert.False(result.Applied);
        var error = Assert.Single(result.Errors);
        Assert.Equal("err.ECR-REG-0422.lookupWrongRegistry", error.MessageKey);

        // ⛔ ent6 R2: код (і назва) запису з іншого, можливо забороненого довідника не потрапляє у відповідь.
        Assert.NotNull(error.Params);
        Assert.DoesNotContain(error.Params!.Values, v => v.Contains(ForeignCode, StringComparison.Ordinal));
        Assert.False(error.Params.ContainsKey("entryCode"));
        Assert.Equal("TARGET", error.Params["expectedRegistry"]);
    }

    private Task<RegistryEntryWriteResult> Write()
        => new RegistryEntryWriter(_registries, _uow, _audit, _user, _clock).WriteAsync(
            new RegistryEntryWriteBatch(
                OwnerId, [new RegistryEntryWrite("NEW1", new Dictionary<string, object?> { ["LINK"] = ForeignEntryId })])
            { CreateOnly = true },
            CancellationToken.None);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);
}
