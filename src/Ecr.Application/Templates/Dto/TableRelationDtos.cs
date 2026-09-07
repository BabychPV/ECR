// src/Ecr.Application/Templates/Dto/TableRelationDtos.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Templates.Dto;

/// <summary>
/// Зв'язок між таблицями версії, як його бачить редактор (<c>ФВ-2.12</c>,
/// <c>ФВ-2.13</c>).
/// </summary>
/// <remarks>
/// ⚠ Коди таблиць їдуть поруч із ідентифікаторами навмисно. Ідентифікатор —
/// те, чим зв'язок адресує таблицю, і показувати його людині безглуздо; код —
/// те, чим таблицю називають у формулах. Клієнт, який складав би підпис сам,
/// мусив би тримати другу мапу «ідентифікатор → код» і показував би порожнє
/// місце щоразу, коли структура ще не дочиталася.
/// </remarks>
/// <param name="Id">Ідентифікатор зв'язку.</param>
/// <param name="Code">Код — його ідентичність і адреса в API.</param>
/// <param name="RelationKind">Вид зв'язку.</param>
/// <param name="SourceTableDefId">Таблиця-джерело.</param>
/// <param name="SourceTableCode">Код таблиці-джерела.</param>
/// <param name="TargetTableDefId">Таблиця-приймач.</param>
/// <param name="TargetTableCode">Код таблиці-приймача.</param>
/// <param name="MatchJson">Як зіставляються рядки джерела і приймача.</param>
/// <param name="MapJson">Які колонки на які; <c>null</c> — перенесення немає.</param>
/// <param name="OnSourceChange">Реакція на зміну джерела: 0 Recalc, 1 Warn, 2 Block.</param>
/// <param name="IsActive">Чи діє зв'язок.</param>
/// <param name="IsEditable">
/// Чи можна правити зв'язок. Відповідь дає СЕРВЕР за станом версії: клієнт,
/// який виводить це сам, тримає другу копію правила «опублікована незмінна»
/// (<c>ФВ-7.1</c>) — і саме вона розійдеться з доменом на третьому стані.
/// </param>
public sealed record TableRelationDto(
    int Id,
    string Code,
    TableRelationKind RelationKind,
    int SourceTableDefId,
    string SourceTableCode,
    int TargetTableDefId,
    string TargetTableCode,
    string MatchJson,
    string? MapJson,
    byte OnSourceChange,
    bool IsActive,
    bool IsEditable);
