using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;

namespace Ecr.TestKit;

/// <summary>
/// Форми довідників у пам'яті — те, що в публікації дасть БД, а в редакторі
/// <c>GET /expressions/metadata</c> (FEATURE-REGISTRY-TABLES §5.9).
/// </summary>
/// <remarks>
/// ⚠ Коди — без урахування регістру, як обіцяє <see cref="IRegistryShapeSource"/>.
/// </remarks>
public sealed class InMemoryRegistryShapes : IRegistryShapeSource
{
    private readonly Dictionary<string, RegistryShape> _registries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Склад потоку форми 301 (HSE301): <c>STREAM</c>, <c>COMPONENT</c>,
    /// <c>STREAM_CASE</c> (первинний ключ <c>STREAM + CASE_NAME</c>) і
    /// <c>GAS_COMPOSITION</c> (кейс × компонент → мол.%).
    /// </summary>
    /// <param name="mwUnit">Одиниця <c>COMPONENT.MW</c>.</param>
    /// <param name="temperatureUnit">Одиниця <c>STREAM_CASE.T_C</c>.</param>
    public static InMemoryRegistryShapes Hse301(int? mwUnit = null, int? temperatureUnit = null)
        => new InMemoryRegistryShapes()
            .Add("STREAM", [Field("NAME", CellDataType.String)])
            .Add("COMPONENT",
                [
                    Field("MW", CellDataType.Decimal, mwUnit),
                    Field("N_C", CellDataType.Int),
                    Field("N_S", CellDataType.Int),
                ])
            .Add("STREAM_CASE",
                [
                    Field("STREAM", CellDataType.Lookup, lookup: "STREAM"),
                    Field("CASE_NAME", CellDataType.String),
                    Field("T_C", CellDataType.Decimal, temperatureUnit),
                    Field("VALID_FROM", CellDataType.Date),
                ],
                [new RegistryKeyShape("PK", true, ["STREAM", "CASE_NAME"])])
            .Add("GAS_COMPOSITION",
                [
                    Field("CASE", CellDataType.Lookup, lookup: "STREAM_CASE"),
                    Field("COMPONENT", CellDataType.Lookup, lookup: "COMPONENT"),
                    Field("MOL_PCT", CellDataType.Decimal),
                ],
                [new RegistryKeyShape("PK", true, ["CASE", "COMPONENT"])]);

    /// <summary>Поле довідника.</summary>
    public static RegistryFieldShape Field(string code, CellDataType type, int? unit = null, string? lookup = null)
        => new(code, type, unit, lookup);

    /// <summary>Додає довідник.</summary>
    public InMemoryRegistryShapes Add(
        string code, IReadOnlyList<RegistryFieldShape> fields, IReadOnlyList<RegistryKeyShape>? keys = null)
    {
        _registries[code] = new RegistryShape(code, fields, keys ?? []);
        return this;
    }

    /// <inheritdoc />
    public RegistryShape? FindRegistry(string registryCode)
    {
        ArgumentNullException.ThrowIfNull(registryCode);
        return _registries.GetValueOrDefault(registryCode);
    }
}
