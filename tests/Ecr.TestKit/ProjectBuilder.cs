using System.Reflection;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.TestKit;

/// <summary>Проєкт із періодами для тестів Етапу 3.</summary>
/// <remarks>
/// Періоди додаються через приватне поле: домен не дає додавати їх ззовні
/// навмисно — календар будує <c>PeriodCalendar</c>, а не той, хто захоче.
/// </remarks>
public static class ProjectBuilder
{
    /// <summary>Стандартна політика: відкриття одразу, жорстке закриття +45 днів.</summary>
    public static PeriodPolicy Policy(int openOffsetDays = 0, int hardCloseOffsetDays = 45)
        => new(EcrCode.Create("STD"), openOffsetDays, graceOffsetDays: 15,
               hardCloseOffsetDays, yearGraceOffsetDays: 45);

    /// <summary>Пояс майданчика без переходу на літній час.</summary>
    public static TimeZoneInfo Zone(int offsetHours = 5)
        => TimeZoneInfo.CreateCustomTimeZone(
            $"SITE{offsetHours:+0;-0}", TimeSpan.FromHours(offsetHours),
            $"Майданчик UTC{offsetHours:+0;-0}", $"Майданчик UTC{offsetHours:+0;-0}");

    /// <summary>Проєкт на повний 2026 рік.</summary>
    public static Project Project(
        PeriodKind kind = PeriodKind.Monthly,
        string timeZoneId = "Asia/Almaty",
        int id = 10)
    {
        var project = new Project(
            EcrCode.Create("PLANT_A"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Plant A" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: 2, kind, periodPolicyId: 1, timeZoneId);

        typeof(Entity<int>).GetProperty("Id")!.SetValue(project, id);
        return project;
    }

    /// <summary>Додає готові періоди в проєкт.</summary>
    public static void Attach(Project project, IEnumerable<Period> periods)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(periods);

        var field = typeof(Project).GetField("_periods", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var list = (List<Period>)field.GetValue(project)!;
        var next = 100;
        foreach (var period in periods)
        {
            typeof(Entity<int>).GetProperty("Id")!.SetValue(period, next++);
            list.Add(period);
        }
    }
}
