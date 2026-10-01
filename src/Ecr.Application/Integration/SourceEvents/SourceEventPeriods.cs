// src/Ecr.Application/Integration/SourceEvents/SourceEventPeriods.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Integration.SourceEvents;

/// <summary>Період проєкту з його межами в UTC.</summary>
/// <param name="PeriodKey">Ключ періоду.</param>
/// <param name="State">Стан.</param>
/// <param name="Bounds">Межі <c>[початок, кінець)</c> у UTC за поясом проєкту.</param>
public sealed record SourceEventPeriod(int PeriodKey, PeriodState State, Period.UtcRange Bounds);

/// <summary>
/// «Початок події → період» за поясом проєкту (V-10 → <c>D-179</c>) і переведення часу події в пояс
/// проєкту для комірок <c>$start</c>/<c>$end</c>.
/// </summary>
/// <remarks>
/// ⛔ Період шукається за МЕЖАМИ (<see cref="Period.UtcBounds"/>), а не за UTC-датою початку: подія
/// о 19:30 UTC 31 січня в Asia/Atyrau (+05:00) — вже 00:30 1 лютого, тобто лютий. Місяць, узятий з
/// UTC-дати, поклав би її в січень і викиди — не в той період.
/// </remarks>
public static class SourceEventPeriods
{
    /// <summary>Знаходить період, у якому лежить початок події.</summary>
    /// <param name="startUtc">Початок події, UTC.</param>
    /// <param name="periods">Періоди проєкту.</param>
    /// <returns>Період чи <c>null</c>, якщо жоден не містить початку.</returns>
    public static SourceEventPeriod? Locate(DateTime startUtc, IReadOnlyList<SourceEventPeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var moment = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        return periods.FirstOrDefault(p => p.Bounds.StartUtc <= moment && moment < p.Bounds.EndUtc);
    }

    /// <summary>
    /// Час події в поясі проєкту, до секунди, без ознаки зони — так його бачить людина в комірці
    /// (§4.7.3: «до секунди, у поясі проєкту»).
    /// </summary>
    /// <param name="utc">Момент, UTC.</param>
    /// <param name="siteTimeZone">Пояс проєкту.</param>
    public static DateTime ToProjectTime(DateTime utc, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(siteTimeZone);

        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), siteTimeZone);
        return new DateTime(local.Ticks - (local.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Unspecified);
    }
}
