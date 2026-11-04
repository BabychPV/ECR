// src/Ecr.Application/Registries/Dto/RegistryEntryDto.cs

using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Dto;

/// <summary>
/// Запис довідника для UI і резолвінгу. У комірці зберігається
/// <see cref="Id"/>, а не <see cref="Display"/> (`ФВ-8.8`) — саме тому
/// перейменування не змінює історичні дані.
/// </summary>
/// <param name="Id">Ідентифікатор запису (це значення зберігається в комірці).</param>
/// <param name="Code">Стабільний код (для дозволу - його номер).</param>
/// <param name="Display">Локалізована назва для показу.</param>
/// <param name="ParentEntryId">Батьківський запис ієрархії.</param>
/// <param name="ValidFrom">Початок чинності.</param>
/// <param name="ValidTo">Кінець чинності.</param>
/// <param name="TypeCode">
/// Лише в піклері групи типів дозволів (<c>GET registries/PRMG_*/entries</c>): код типу дозволу запису; інакше <c>null</c>.
/// </param>
/// <param name="TypeName">
/// Назва типу мовою користувача (підпис «номер — тип»); лише в піклері групи, інакше <c>null</c>.
/// </param>
public sealed record RegistryEntryDto(
    long Id,
    string Code,
    string Display,
    long? ParentEntryId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? TypeCode = null,
    string? TypeName = null);

/// <summary>Створення або оновлення запису довідника.</summary>
/// <param name="Id"><c>null</c> — створення нового запису; інакше — оновлення наявного.</param>
/// <param name="RegistryDefId">Довідник, до якого належить запис.</param>
/// <param name="Code">Стабільний код; не змінюється при перейменуванні (`ФВ-8.8`).</param>
/// <param name="Display">Локалізована назва для показу.</param>
/// <param name="ParentEntryId">Батьківський запис в ієрархії; <c>null</c> — корінь.</param>
/// <param name="Values">Значення полів: код поля → значення відповідного типу.</param>
/// <param name="LevelCode">
/// Level code in a hierarchical registry (required for a new entry there, ignored in a flat registry);
/// <c>null</c> on update keeps the current level.
/// </param>
public sealed record RegistryEntryUpsertDto(
    long? Id,
    int RegistryDefId,
    string Code,
    LocalizedText Display,
    long? ParentEntryId,
    IReadOnlyDictionary<string, object?> Values,
    string? LevelCode = null);

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
/// <param name="LevelCode">Level code in a hierarchical registry; <c>null</c> in a flat one.</param>
public sealed record RegistryEntryDetailDto(
    long Id,
    string Code,
    LocalizedText DisplayL10n,
    long? ParentEntryId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    IReadOnlyDictionary<string, string?> Values,
    string? LevelCode = null);
