// src/Ecr.Application/Workflow/ReturnedSheets/ReturnedSheetsDtos.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow.ReturnedSheets;

/// <summary>
/// ✎ D-449: аркуш, повернений на доопрацювання (відхилення погоджувачем або повернення в роботу), що чекає ПОТОЧНОГО користувача
/// («Повернуто на доопрацювання» у «My tasks»).
/// </summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="DocumentNameL10n">Назва документа; <c>null</c> — не задана (показується ключ).</param>
/// <param name="ProjectCode">Код проєкту документа.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="SheetCode">Код аркуша (для посилання на аркуш).</param>
/// <param name="SheetNameL10n">Назва аркуша.</param>
/// <param name="PeriodKey">Ключ періоду <c>Рік*100+№</c>.</param>
/// <param name="ReturnedAt">Коли повернуто, UTC.</param>
/// <param name="ReturnedByDisplayName">Хто повернув; <c>null</c> — користувача вже немає.</param>
/// <param name="Reason">Причина повернення (обов'язкова і при відхиленні, і при поверненні в роботу); може бути багаторядковою.</param>
public sealed record ReturnedSheetItemDto(
    long DocumentId,
    string DocumentKey,
    LocalizedText? DocumentNameL10n,
    string ProjectCode,
    int SheetDefId,
    string SheetCode,
    LocalizedText SheetNameL10n,
    int PeriodKey,
    DateTime ReturnedAt,
    string? ReturnedByDisplayName,
    string Reason);

/// <summary>Сторінка блоку «Повернуто на доопрацювання»: найдавніші повернення першими.</summary>
/// <param name="Items">Повернені аркуші, над якими має працювати поточний користувач.</param>
/// <param name="NextCursor">Непрозорий курсор наступної сторінки; <c>null</c> — кінець.</param>
public sealed record ReturnedSheetsDto(IReadOnlyList<ReturnedSheetItemDto> Items, string? NextCursor);
