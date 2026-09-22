using Ecr.Application.Reporting.Dto;

namespace Ecr.Application.Reporting;

/// <summary>
/// «Хто затримує кампанію»: класифікація проєкту за строком подання. Одне
/// джерело правди для рядків огляду й для підсумків по всіх проєктах.
/// </summary>
/// <remarks>
/// Рішення людини (підтверджено в чаті клієнтської сесії, 2026-09-21): ТЗ
/// формального визначення не має (у прототипі — лише <c>late: bool</c>), тож
/// «затримує» — це прострочення відносно строку, а не неповнота: інакше в
/// перший день періоду затримували б усі, і перелік був би марним.
///
/// ⚠ Строк — момент у UTC, уже порахований у поясі проєкту
/// (<c>Period.RecomputeBoundaries</c>, <c>D-68</c>), тож «минув» — пряме
/// порівняння моментів. А от «скільки днів лишилося» — це календарні доби
/// ПОЯСУ ПРОЄКТУ: у UTC у поясі +5 строк «падав» би на добу раніше.
/// </remarks>
public static class CampaignProgressRule
{
    /// <summary>Чи всі документи проєкту затверджено (порожній проєкт — ні).</summary>
    /// <param name="documents">Документів у проєкті.</param>
    /// <param name="approved">Із них повністю затверджених.</param>
    public static bool AllApproved(int documents, int approved) => documents > 0 && approved == documents;

    /// <summary>Класифікує проєкт.</summary>
    /// <param name="allApproved">Усі документи затверджено.</param>
    /// <param name="hasSnapshot">Є хоча б один поточний зріз — підсумковий артефакт для регулятора.</param>
    /// <param name="deadlineUtc">Строк подання (виключно); <c>null</c> — межі ще не пораховано.</param>
    /// <param name="zone">Пояс проєкту.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <param name="atRiskDays">Скільки днів до останнього дня подання вважаються ризиком.</param>
    public static CampaignProgress Classify(
        bool allApproved, bool hasSnapshot, DateTime? deadlineUtc, TimeZoneInfo zone, DateTime utcNow, int atRiskDays)
    {
        ArgumentNullException.ThrowIfNull(zone);

        // ⛔ Зріз обов'язковий: затверджені документи без зрізу — регулятор ще
        // не отримав нічого.
        if (allApproved && hasSnapshot)
        {
            return CampaignProgress.Done;
        }

        if (deadlineUtc is not { } deadline)
        {
            return CampaignProgress.InProgress;
        }

        if (utcNow >= deadline)
        {
            return CampaignProgress.Overdue;
        }

        // Строк — опівніч у поясі проєкту, тож останній день подання — доба
        // перед ним. 0 означає «сьогодні останній день».
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utcNow), zone));
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(AsUtc(deadline), zone)).AddDays(-1);

        return lastDay.DayNumber - today.DayNumber <= atRiskDays
            ? CampaignProgress.AtRisk
            : CampaignProgress.InProgress;
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>Налаштування класифікації кампанії (<c>Campaign:AtRiskDays</c>).</summary>
/// <param name="AtRiskDays">Поріг «під ризиком» у днях до останнього дня подання; типово 3.</param>
public sealed record CampaignProgressPolicy(int AtRiskDays)
{
    /// <summary>Поріг за замовчуванням.</summary>
    public const int DefaultAtRiskDays = 3;
}
