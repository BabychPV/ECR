using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;

namespace Ecr.Application.Tests;

/// <summary>
/// Довідник <c>Permits</c> з одним полем <c>Limit</c> (Decimal) на підробленому
/// <see cref="IRegistryStore"/> — для <c>REGFIELD</c> у правилах валідації (D16-04).
/// </summary>
internal static class RegistryTestData
{
    /// <summary>Id визначення поля <c>Limit</c>.</summary>
    public const int LimitFieldDefId = 1;

    /// <summary>Налаштовує <paramref name="registries"/>: запис <paramref name="entryId"/> має <c>Limit = limit</c>.</summary>
    public static void PermitLimit(IRegistryStore registries, int registryDefId, long entryId, decimal limit)
    {
        var definition = new RegistryDef(
            EcrCode.Create("Permits"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Permits" }),
            isTemporal: false);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(definition, registryDefId);

        var field = new RegistryFieldDef(
            registryDefId, EcrCode.Create("Limit"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Limit" }), CellDataType.Decimal, ordinal: 0);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(field, LimitFieldDefId);
        definition.AddField(field);

        registries.FindDefinitionByIdAsync(registryDefId, Arg.Any<CancellationToken>()).Returns(definition);

        var value = new RegistryValue(entryId, LimitFieldDefId);
        value.Set(CellDataType.Decimal, limit, unitId: null);
        registries.ListValuesAsync(entryId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[value]);
    }
}
