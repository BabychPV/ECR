using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Registries;

/// <summary>
/// Дані довідників, прочитані завантажувачем один раз (<see cref="IRegistrySnapshotSource"/>, L5-12):
/// <see cref="Build"/> дає <see cref="RegistrySnapshot.Create"/> на дату без нового звернення до БД.
/// </summary>
/// <param name="registries">Довідники разом із полями.</param>
/// <param name="primaryKeys">Активні первинні ключі.</param>
/// <param name="entries">Записи «станом на» системний момент — усі, без фільтра видимості.</param>
/// <param name="values">Значення полів цих записів.</param>
public sealed class RegistrySnapshotSource(
    IReadOnlyList<RegistryDef> registries,
    IReadOnlyList<RegistryKeyDef> primaryKeys,
    IReadOnlyList<RegistryEntry> entries,
    IReadOnlyList<RegistrySnapshotValue> values) : IRegistrySnapshotSource
{
    /// <summary>Джерело без жодного довідника: кожен <see cref="Build"/> дає <see cref="RegistrySnapshot.Empty"/>.</summary>
    public static IRegistrySnapshotSource Empty { get; } = new EmptySource();

    /// <inheritdoc />
    public IRegistrySnapshot Build(DateOnly businessDate)
        => RegistrySnapshot.Create(registries, primaryKeys, entries, values, businessDate);

    private sealed class EmptySource : IRegistrySnapshotSource
    {
        public IRegistrySnapshot Build(DateOnly businessDate) => RegistrySnapshot.Empty;
    }
}
