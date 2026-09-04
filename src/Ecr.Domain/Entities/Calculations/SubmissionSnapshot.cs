// src/Ecr.Domain/Entities/Calculations/SubmissionSnapshot.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Іммутабельний зліпок вхідних значень на момент подання (ФВ-5.7).
/// </summary>
/// <remarks>
/// **Саме він, а не трейс, є доказовою базою** (ЗБР-3). Трейс показує, як
/// рахували; зліпок показує, що саме рахували. Через п'ять років на питання
/// «звідки ця цифра» відповідає він — незалежно від того, що відбулося з
/// довідниками, методологіями і самими даними потім.
/// <para>
/// Не редагується ніколи. `Reopen` створює **новий** зліпок, старий лишається
/// (D-67, ФВ-9.17).
/// </para>
/// </remarks>
public sealed class SubmissionSnapshot : Entity<long>
{
    private SubmissionSnapshot() { }

    public SubmissionSnapshot(long documentId, int sheetDefId, int periodKey, int submittedByUserId, DateTime utcNow)
    {
        DocumentId = documentId;
        SheetDefId = sheetDefId;
        PeriodKey = periodKey;
        SubmittedByUserId = submittedByUserId;
        SubmittedAt = utcNow;
    }

    public long DocumentId { get; private set; }
    public int SheetDefId { get; private set; }
    public int PeriodKey { get; private set; }
    public int SubmittedByUserId { get; private set; }
    public DateTime SubmittedAt { get; private set; }

    /// <summary>Зліпок значень. Формат — за `02a`; стискається при записі.</summary>
    public byte[] PayloadCompressed { get; private set; } = [];

    /// <summary>Версії, чинні на момент подання: шаблон, методології, реєстри.</summary>
    public string VersionsJson { get; private set; } = null!;

    /// <summary>Контрольна сума — доводить, що зліпок не змінювався.</summary>
    public string Checksum { get; private set; } = null!;
}
