// tests/Ecr.Application.Tests/Registries/RegistryFieldSnapshotLoaderBatchTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Аудит P9: знімок полів довідника для кількох записів читає значення ОДНИМ пакетним викликом
/// сховища, а не викликом на кожен запис.
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути в <c>RegistryFieldSnapshotLoader</c> <c>ListValuesAsync</c> на кожен
/// запис → <see cref="Кілька_записів_читаються_одним_пакетним_викликом"/> червоний (виклики
/// поштучного методу є, пакетного — немає). Звернення на справжньому SQL —
/// <c>RegistryValuesBatchReadTests</c> (Infrastructure).
/// </remarks>
public sealed class RegistryFieldSnapshotLoaderBatchTests
{
    private const int RegistryDefId = 910;
    private const int LimitFieldId = 11;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    [Fact]
    [Trait("Requirement", "ФВ-5.8")]
    [Trait("Finding", "P9")]
    public async Task Кілька_записів_читаються_одним_пакетним_викликом()
    {
        _registries.FindDefinitionByIdAsync(RegistryDefId, Arg.Any<CancellationToken>()).Returns(Definition());
        _registries.ListValuesForEntriesAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[Limit(101, 1m), Limit(102, 2m)]);

        // Три записи, один із них двічі; запис 103 значення не має.
        var snapshot = await RegistryFieldSnapshotLoader.LoadAsync(
            _registries,
            [Request(101), Request(102), Request(103), Request(101)],
            CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(ExpressionValue.Number(1m), snapshot![101]["Limit"]);
        Assert.Equal(ExpressionValue.Number(2m), snapshot[102]["Limit"]);
        Assert.False(snapshot.ContainsKey(103));

        await _registries.Received(1).ListValuesForEntriesAsync(
            Arg.Is<IReadOnlyCollection<long>>(ids => ids.Order().SequenceEqual(new long[] { 101, 102, 103 })),
            Arg.Any<CancellationToken>());
        await _registries.DidNotReceive().ListValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _registries.Received(1).FindDefinitionByIdAsync(RegistryDefId, Arg.Any<CancellationToken>());
    }

    private static RegistryFieldRequest Request(long entryId) => new(entryId, RegistryDefId, "Limit");

    private static RegistryValue Limit(long entryId, decimal value)
    {
        var stored = new RegistryValue(entryId, LimitFieldId);
        stored.Set(CellDataType.Decimal, value, unitId: null);
        return stored;
    }

    private static RegistryDef Definition()
    {
        var definition = new RegistryDef(EcrCode.Create("Permits"), Text("Permits"), isTemporal: false);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, RegistryDefId);

        var field = new RegistryFieldDef(RegistryDefId, EcrCode.Create("Limit"), Text("Limit"), CellDataType.Decimal, ordinal: 0);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(field, LimitFieldId);
        definition.AddField(field);
        return definition;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
