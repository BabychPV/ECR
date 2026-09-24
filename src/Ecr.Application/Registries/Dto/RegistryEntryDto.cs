// src/Ecr.Application/Registries/Dto/RegistryEntryDto.cs

using Ecr.Domain.ValueObjects;

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

/// <summary>Створення або оновлення запису довідника.</summary>
/// <param name="Id"><c>null</c> — створення нового запису; інакше — оновлення наявного.</param>
/// <param name="RegistryDefId">Довідник, до якого належить запис.</param>
/// <param name="Code">Стабільний код; не змінюється при перейменуванні (`ФВ-8.8`).</param>
/// <param name="Display">Локалізована назва для показу.</param>
/// <param name="ParentEntryId">Батьківський запис в ієрархії; <c>null</c> — корінь.</param>
/// <param name="Values">Значення полів: код поля → значення відповідного типу.</param>
public sealed record RegistryEntryUpsertDto(
    long? Id,
    int RegistryDefId,
    string Code,
    LocalizedText Display,
    long? ParentEntryId,
    IReadOnlyDictionary<string, object?> Values);

/// <summary>Один запис довідника цілком — для форми правки (X-03, R-04).</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Стабільний код.</param>
/// <param name="DisplayL10n">
/// Назва ВСІМА мовами каталогу. ⛔ Саме цього бракувало формі: перелік несе
/// назву однією мовою, і збереження з нього стирало переклади.
/// </param>
/// <param name="ParentEntryId">Батьківський запис; <c>null</c> — корінь.</param>
/// <param name="ValidFrom">Початок вікна чинності.</param>
/// <param name="ValidTo">Кінець вікна чинності.</param>
/// <param name="Values">
/// Значення полів: код поля → текст в інваріантному форматі, який приймає
/// <c>POST …/entries</c>; <c>null</c> — поле не заповнене.
/// </param>
public sealed record RegistryEntryDetailDto(
    long Id,
    string Code,
    LocalizedText DisplayL10n,
    long? ParentEntryId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    IReadOnlyDictionary<string, string?> Values);
