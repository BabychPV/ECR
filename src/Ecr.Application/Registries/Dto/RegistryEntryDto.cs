// src/Ecr.Application/Registries/Dto/RegistryEntryDto.cs
namespace Ecr.Application.Registries.Dto;

/// <summary>
/// Запис довідника для UI і резолвінгу. У комірці зберігається
/// <see cref="Id"/>, а не <see cref="Display"/> (`ФВ-8.8`) — саме тому
/// перейменування не змінює історичні дані.
/// </summary>
public sealed record RegistryEntryDto(
    long Id,
    string Code,
    string Display,
    long? ParentEntryId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo);

/// <param name="Values">Значення полів: код поля → значення відповідного типу.</param>
public sealed record RegistryEntryUpsertDto(
    long? Id,
    int RegistryDefId,
    string Code,
    LocalizedText Display,
    long? ParentEntryId,
    IReadOnlyDictionary<string, object?> Values);
