// tests/Ecr.Application.Tests/Registries/RegistryFieldSnapshotLoaderLookupUnitTests.cs
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
/// RT-24: знімок полів довідника читає поля типів <c>Lookup</c> (id цільового запису) і
/// <c>Unit</c> (код одиниці), які <c>REGFIELD</c> раніше пропускав.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати гілки <c>CellDataType.Lookup</c>/<c>CellDataType.Unit</c> у
/// <c>RegistryFieldSnapshotLoader.ToExpressionValue</c> — обидва тести червоні (поле відсутнє
/// у знімку).
/// </remarks>
public sealed class RegistryFieldSnapshotLoaderLookupUnitTests
{
    private const int RegistryDefId = 920;
    private const int ParentFieldId = 21;
    private const int UnitFieldId = 22;
    private const long EntryId = 201;

    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();

    [Fact]
    [Trait("Requirement", "ФВ-8.1")]
    public async Task Поле_Lookup_читається_як_id_цільового_запису()
    {
        Arrange();
        var value = new RegistryValue(EntryId, ParentFieldId);
        value.Set(CellDataType.Lookup, 777L, unitId: null);
        _registries.ListValuesAsync(EntryId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[value]);

        var snapshot = await RegistryFieldSnapshotLoader.LoadAsync(
            _registries, [new RegistryFieldRequest(EntryId, RegistryDefId, "Parent")], CancellationToken.None);

        Assert.Equal(ExpressionValue.Number(777m), snapshot![EntryId]["Parent"]);
    }

    [Fact]
    [Trait("Requirement", "ФВ-8.1")]
    public async Task Поле_Unit_читається_як_код_одиниці_зі_знімка_одиниць()
    {
        Arrange();
        var value = new RegistryValue(EntryId, UnitFieldId);
        value.Set(CellDataType.Unit, 5, unitId: null);
        _registries.ListValuesAsync(EntryId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryValue>)[value]);
        var units = new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new UnitRef(5, "kg", 1) },
            new Dictionary<string, int>());

        var snapshot = await RegistryFieldSnapshotLoader.LoadAsync(
            _registries, [new RegistryFieldRequest(EntryId, RegistryDefId, "Mass")], CancellationToken.None, units);

        Assert.Equal(ExpressionValue.Text("kg"), snapshot![EntryId]["Mass"]);
    }

    private void Arrange()
    {
        var definition = new RegistryDef(EcrCode.Create("Streams"), Text("Streams"), isTemporal: false);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(definition, RegistryDefId);
        AddField(definition, "Parent", CellDataType.Lookup, ParentFieldId, 0);
        AddField(definition, "Mass", CellDataType.Unit, UnitFieldId, 1);
        _registries.FindDefinitionByIdAsync(RegistryDefId, Arg.Any<CancellationToken>()).Returns(definition);
    }

    private static void AddField(RegistryDef definition, string code, CellDataType type, int id, int ordinal)
    {
        var field = new RegistryFieldDef(RegistryDefId, EcrCode.Create(code), Text(code), type, ordinal);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(field, id);
        definition.AddField(field);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
