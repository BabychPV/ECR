// src/Ecr.Application/Ports/PermitTypeSummary.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Короткий знімок типу дозволу для переліків і пошуку за реєстром шапки: усе, що треба, КРІМ тексту чернетки
/// (PA2-S-01: чернетка до 2 Mі символів, її читання й розбір на кожен запит переліку множилися на кількість типів).
/// </summary>
/// <param name="Id">Ідентифікатор типу.</param>
/// <param name="Code">Код типу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Category">Категорія.</param>
/// <param name="HeaderRegistryDefId">Реєстр шапки.</param>
/// <param name="Version">Опублікована версія (для ніколи не опублікованого - майбутня перша).</param>
/// <param name="Status">Стан.</param>
/// <param name="UpdatedAt">Остання зміна чернетки чи публікація, UTC.</param>
/// <param name="UpdatedByUserId">Хто змінив.</param>
/// <param name="HasDraft">Чи є чернетка (<c>DraftJson IS NOT NULL</c> - без читання тексту).</param>
/// <param name="SmallDraftJson">
/// Текст чернетки, ЛИШЕ якщо вона не більша за <c>PermitTypeLimits.ListDraftSummaryMaxChars</c>; інакше <c>null</c>
/// (SQL не віддає її взагалі).
/// </param>
public sealed record PermitTypeSummary(
    int Id,
    string Code,
    LocalizedText NameL10n,
    PermitCategory Category,
    int HeaderRegistryDefId,
    int Version,
    PermitTypeStatus Status,
    DateTime? UpdatedAt,
    int? UpdatedByUserId,
    bool HasDraft,
    string? SmallDraftJson)
{
    /// <summary>Знімок із завантаженого типу (для шляхів, що вже мають сутність).</summary>
    /// <param name="type">Тип.</param>
    public static PermitTypeSummary From(PermitTypeDef type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return new PermitTypeSummary(
            type.Id, type.Code, type.NameL10n, type.Category, type.HeaderRegistryDefId, type.Version, type.Status,
            type.UpdatedAt, type.UpdatedByUserId, type.HasDraft, type.DraftJson);
    }
}
