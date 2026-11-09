using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Periods;

/// <summary>
/// Доганяє збережені стани періодів проєкту до ефективних — одразу, а не наступним прогоном
/// <c>PeriodStateJob</c> (UAT 08.11, сценарій e).
/// </summary>
/// <remarks>
/// ⛔ Слово людини 08.11: «кнопки реопен немає». Збереження політики зсувів (чи зміна поточного періоду) лишало
/// збережений <c>Open</c> місяцям, чий новий строк блокування давно минув (березень), до прогону задачі станів:
/// сторінка Periods показувала «Open · closes today» і не давала Reopen, хоча сітка вже не приймала запису
/// (рішення про запис бере ефективний стан, F-08). Тепер — той самий шлях, що в архівації (F-08,
/// <c>ArchiveProjectHandler</c>): <see cref="PeriodStateCalculator.Effective"/> → <see cref="Period.AdvanceTo"/>,
/// лише для активного проєкту (неактивні задача станів теж не веде). ✎ F13 e1 (09.11): незаблоковані й
/// неперевідкриті місяці — в обидва боки (<see cref="Period.RederiveTo"/>), <c>Closed</c> — лише вперед.
/// <para>
/// ⚠ На відміну від архівації, кожен перехід АУДИТУЄТЬСЯ (<c>aud.StructureChange</c>, <c>Period</c> /
/// <c>AdvanceState</c>, причина — дія людини): перехід стався від її дії, а не від годинника.
/// </para>
/// <para>
/// ⚠ Системний Reopen «вікна року» (D-204) лишається задачі станів: це окремий шлях із власною причиною.
/// </para>
/// </remarks>
public static class PeriodStateCatchUp
{
    /// <summary>Тип сутності в <c>aud.StructureChange</c>.</summary>
    public const string AuditEntityType = "Period";

    /// <summary>Операція в <c>aud.StructureChange</c>.</summary>
    public const string AuditOperation = "AdvanceState";

    private static readonly PeriodStateCalculator States = new();

    /// <summary>Перехід одного періоду.</summary>
    /// <param name="Period">Період.</param>
    /// <param name="Before">Стан до.</param>
    /// <param name="After">Стан після.</param>
    public readonly record struct Advanced(Period Period, PeriodState Before, PeriodState After);

    /// <summary>
    /// Просуває стани періодів <paramref name="project"/> до ефективних на <paramref name="utcNow"/> і пише аудит
    /// кожного переходу. Нічого не зберігає — це робить викликач у своїй транзакції.
    /// </summary>
    /// <param name="project">Проєкт із завантаженими періодами (відстежується контекстом).</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <param name="audit">Журнал (та сама транзакція).</param>
    /// <param name="userId">Автор дії, від якої стався перехід.</param>
    /// <param name="reason">Причина в журналі (що зробила людина).</param>
    /// <param name="correlationId">Кореляція запиту.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Застосовані переходи (порожньо — нічого не змінилось або проєкт не активний).</returns>
    public static async Task<IReadOnlyList<Advanced>> ApplyAsync(
        Project project,
        DateTime utcNow,
        IAuditWriter audit,
        int userId,
        string reason,
        string? correlationId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(audit);

        if (project.Status != ProjectStatus.Active)
        {
            return [];
        }

        // ⚠ ФВ-1.8: те саме річне вікно, що в задачі станів і в рішенні про запис.
        var yearGrace = YearGraceWindow.For(
            project.PeriodEnd,
            project.YearGraceOffsetDays,
            SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());

        var advanced = new List<Advanced>();

        foreach (var period in project.Periods)
        {
            var before = period.State;

            // ⛔ F13 e1 (09.11): відпущена політика (15/45 після 3/20) лишала жовтень «Late», хоча за нею він Open
            // — `Effective` веде лише вперед. Незаблокований і неперевідкритий місяць тепер отримує стан, який
            // політика дає ЗАРАЗ, в обидва боки; `Closed` і перевідкритий — як і раніше, лише вперед.
            var target = period.AcceptsPolicyRederive
                ? States.Calculate(period, utcNow, TimeZoneInfo.Utc, yearGrace)
                : States.Effective(period, utcNow, yearGrace);

            if (target == before)
            {
                continue;
            }

            period.RederiveTo(target, utcNow);
            advanced.Add(new Advanced(period, before, period.State));

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    utcNow,
                    TemplateVersionId: project.TemplateVersionId,
                    EntityType: AuditEntityType,
                    EntityId: period.Id,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: AuditOperation,
                    OldJson: JsonSerializer.Serialize(new { state = before.ToString(), periodKey = period.PeriodKeyValue }),
                    NewJson: JsonSerializer.Serialize(new { state = period.State.ToString(), periodKey = period.PeriodKeyValue }),
                    ChangeReason: reason,
                    ChangedByUserId: userId,
                    CorrelationId: correlationId),
                ct).ConfigureAwait(false);
        }

        // Pinned не чіпається (D-77) — як у задачі станів.
        if (advanced.Count > 0 && project.CurrentPeriodMode == CurrentPeriodMode.Auto)
        {
            project.SetCurrentPeriodAutomatically(States.SelectCurrentPeriod(project.Periods)?.Id, utcNow);
        }

        return advanced;
    }

    /// <summary>Періоди, переходи яких потребують матеріалізації (відкриття), — як у задачі станів.</summary>
    /// <param name="advanced">Застосовані переходи.</param>
    public static IReadOnlyList<int> ToMaterialize(IReadOnlyList<Advanced> advanced)
        => [.. advanced
            .Where(a => PeriodMaterializationTrigger.Requires(a.Before, a.After))
            .Select(a => a.Period.PeriodKeyValue)];
}
