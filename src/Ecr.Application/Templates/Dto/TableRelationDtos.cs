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
    bool IsActive);

/// <summary>Зв'язки версії разом із відповіддю на «чи можна їх правити».</summary>
/// <remarks>
/// ⛔ Конверт, а не голий масив, і причина в порожньому переліку. Механізм
/// опційний (<c>ФВ-2.12</c>), тому версія без жодного зв'язку — звичайний,
/// найчастіший випадок; масив у цьому разі не несе ЖОДНОЇ інформації про стан
/// версії. Клієнт, який мав би вивести <c>isEditable</c> з елементів, на
/// порожньому переліку не вивів би нічого — і показав би кнопку «новий
/// зв'язок» на опублікованій версії, де сервер однаково відмовить
/// (<c>ECR-TMPL-0409</c>). Показана й непрацездатна кнопка гірша за відсутню.
///
/// ⚠ Стан рахує СЕРВЕР. Клієнт, який виводить його зі <c>status</c> версії,
/// тримає другу копію правила «опублікована незмінна» (<c>ФВ-7.1</c>) — і саме
/// вона розійдеться з доменом на третьому стані версії (<c>D2-151</c>).
/// </remarks>
/// <param name="IsEditable">Чи дозволяє стан версії структурну правку.</param>
/// <param name="Relations">Зв'язки в порядку коду; порожньо — таблиці незалежні.</param>
public sealed record TableRelationsDto(
    bool IsEditable,
    IReadOnlyList<TableRelationDto> Relations);
