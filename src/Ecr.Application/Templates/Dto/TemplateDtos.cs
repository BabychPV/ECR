using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates.Dto;

/// <summary>
/// DTO переліків шаблонів і версій.
/// </summary>
/// <remarks>
/// Тут — <b>лише</b> те, що потрібно спискам. Структура опублікованої версії
/// живе окремо в <see cref="TemplateStructureDto"/>, бо в неї інший розмір і
/// власний ключ кешу <c>v{id}:r{rev}</c>: тягнути її в список означало б
/// віддавати мегабайти там, де потрібні назви.
/// </remarks>
public static class TemplateDtoDocs;

/// <summary>Шаблон у списку.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код, унікальний у системі.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="PeriodKind">Періодичність, під яку шаблон розрахований.</param>
/// <param name="IsActive">Неактивний шаблон не пропонується при створенні документів.</param>
/// <param name="PublishedVersionCount">Скільки версій опубліковано.</param>
public sealed record TemplateDto(
    int Id,
    string Code,
    LocalizedText NameL10n,
    PeriodKind PeriodKind,
    bool IsActive,
    int PublishedVersionCount);

/// <summary>Версія шаблону у списку.</summary>
/// <remarks>
/// <paramref name="PresentationRevision"/> входить у ключ кешу структури, тому
/// клієнт має бачити його тут — інакше він не знає, що структуру треба
/// перезапитати після презентаційної правки.
/// </remarks>
/// <param name="Id">Ідентифікатор версії.</param>
/// <param name="TemplateId">Шаблон.</param>
/// <param name="VersionNumber">Номер версії.</param>
/// <param name="Status">Чернетка, опублікована чи виведена з обігу.</param>
/// <param name="PresentationRevision">Ревізія презентаційного шару.</param>
/// <param name="PublishedAt">Коли опубліковано; <c>null</c> для чернетки.</param>
/// <param name="PublishedByUserId">Хто опублікував; <c>null</c> для чернетки.</param>
public sealed record TemplateVersionDto(
    int Id,
    int TemplateId,
    string VersionNumber,
    TemplateVersionStatus Status,
    int PresentationRevision,
    DateTime? PublishedAt,
    int? PublishedByUserId);
