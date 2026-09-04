// src/Ecr.Application/Registries/GetRegistryEntriesHandler.cs
using Ecr.Application.Ports;

namespace Ecr.Application.Registries;

/// <summary>
/// Записи довідника **станом на дату періоду**, а не «активні зараз»
/// (ФВ-8.5).
/// </summary>
/// <remarks>
/// Різниця принципова: документ за березень має бачити дозволи, чинні в
/// березні, навіть якщо сьогодні жовтень і половина з них уже недійсна.
/// </remarks>
public sealed class GetRegistryEntriesHandler(IMetadataCache metadata, RegistryResolver resolver)
{
    public Task<IReadOnlyList<RegistryEntryDto>> HandleAsync(
        string registryCode, DateOnly asOf, long? parentEntryId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: 1) резолвити через RegistryResolver: темпоральність + каскад;\n" +
            "2) parentEntryId фільтрує каскадом (ФВ-8.4): WaterBody звужується " +
            "   вибраним Permit;\n" +
            "3) кеш за ключем {registryCode}:{DataRevision}:{asOf}; DataRevision " +
            "   інкрементує UpsertRegistryEntryHandler — та сама схема, що з " +
            "   метаданими (ФВ-2.5);\n" +
            "4) віддавати Id і Display; у комірці зберігається Id (ФВ-8.8).");
}
