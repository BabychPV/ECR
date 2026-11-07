// src/Ecr.Application/Calculations/RecalculationSkips.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Calculations;

/// <summary>
/// UAT-recalc-ux: аркуш, який перерахунок ПРОПУСТИВ, бо він ЗАТВЕРДЖЕНИЙ (слово людини 07.11: «тільки погоджені не мають перераховуватися»).
/// </summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="DocumentCode">Код документа (<c>BusinessKey</c>).</param>
/// <param name="DocumentName">Назва документа мовами каталогу; <c>null</c> — немає.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="SheetName">Назва аркуша мовами каталогу.</param>
/// <param name="PeriodKey">Період (<c>yyyyMM</c>).</param>
/// <param name="State"><c>Approved</c>.</param>
public sealed record SkippedSheet(
    long DocumentId,
    string DocumentCode,
    LocalizedText? DocumentName,
    int SheetDefId,
    string SheetCode,
    LocalizedText SheetName,
    int PeriodKey,
    string State);

/// <summary>
/// UAT-recalc-ux: підсумок запиту на перерахунок — задача (якщо є що рахувати) і пропущені аркуші.
/// </summary>
/// <param name="JobId">Задача; <c>null</c> — рахувати нічого: усі аркуші області затверджені.</param>
/// <param name="Skipped">Пропущені аркуші (до <see cref="RecalculationSkips.MaxListed"/>).</param>
/// <param name="SkippedTruncated">Пропущених більше, ніж у переліку.</param>
public sealed record RecalculationRequestOutcome(
    string? JobId, IReadOnlyList<SkippedSheet> Skipped, bool SkippedTruncated)
{
    /// <summary>Рахувати нічого: задачу не поставлено.</summary>
    public bool NothingToRecalculate => JobId is null;
}

/// <summary>Спільні межі переліку пропущених аркушів.</summary>
public static class RecalculationSkips
{
    /// <summary>Скільки пропущених аркушів віддається у відповіді; решта — ознакою <c>skippedTruncated</c>.</summary>
    /// <remarks>
    /// ⚠ Перерахунок проєкту за рік — це документи × аркуші × 12 періодів; людині потрібні не тисячі рядків, а
    /// зрозуміти, ЩО пропущено і куди перейти. Повний стан — у переліку документів (фільтр стану).
    /// </remarks>
    public const int MaxListed = 200;

    /// <summary>Тип сутності журналу змін (<c>aud.StructureChange.EntityType</c>) для записів аркуша перерахунку.</summary>
    public const string AuditEntityType = "SheetRecalc";

    /// <summary>Операція журналу: затверджений аркуш пропущено.</summary>
    public const string AuditSkippedApproved = "SkippedApproved";

    /// <summary>Операція журналу: поданий аркуш перераховано після подання (позначка для погоджувача).</summary>
    public const string AuditRecalculatedAfterSubmission = "RecalculatedAfterSubmission";

    /// <summary>Кореляція запису журналу: аркуш документа за період — за нею читається позначка.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    public static string AuditCorrelation(long documentId, int sheetDefId, int periodKey)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"sheet-recalc:{documentId}:{sheetDefId}:{periodKey}");

    /// <summary>Перелік із сховища (стеля + 1) → обрізаний перелік і ознака обрізання.</summary>
    /// <param name="rows">Рядки сховища (до <see cref="MaxListed"/> + 1).</param>
    internal static (IReadOnlyList<SkippedSheet> Items, bool Truncated) Cap(IReadOnlyList<SkippedSheet> rows)
    {
        var items = rows.Take(MaxListed).ToList();
        return (items, rows.Count > MaxListed);
    }
}
