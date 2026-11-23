// src/Ecr.Application/Ports/IOpenedPeriodRecalculation.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// ✎ D-575: перерахунок документів періоду, який щойно вийшов зі <c>Scheduled</c> активного проєкту.
/// </summary>
/// <remarks>
/// ⛔ Формули не відкритого періоду не обчислюються (<c>RecalculationService</c>, D-575), а правки в ньому немає — без
/// цієї постановки місяць після відкриття лишався б порожнім до першої правки чи нічного прогону, і помилки D-506 (#N/A
/// газу без даних HSE400) не з'явились би вчасно. Одна точка для всіх переходів: задача станів (<c>PeriodStateJob</c>) і
/// доганяння станів після зміни політики зсувів (<c>UpdatePeriodPolicyHandler</c>). Активація чернетки сюди не ходить:
/// у чернетці D-575 не діє (формули рахувались), тож відкриття нічого пропущеного не лишає.
/// <para>
/// ⚠ Викликається ПІСЛЯ коміту переходу й не валить викликача: черга не транзакційна, а збій постановки (журнал) лише
/// відкладає перерахунок до нічного розкладу.
/// </para>
/// </remarks>
public interface IOpenedPeriodRecalculation
{
    /// <summary>Ставить перерахунок кожного документа проєкту, що має таблиці в кожному з періодів.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKeys">Періоди, що вийшли зі <c>Scheduled</c> (<see cref="PeriodOpenedRecalculationTrigger"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RequestAsync(int projectId, IReadOnlyCollection<int> periodKeys, CancellationToken ct);
}

/// <summary>✎ D-575: правило «перехід відкриває формули періоду».</summary>
public static class PeriodOpenedRecalculationTrigger
{
    /// <summary>
    /// <c>Scheduled → Open/Grace</c> (⚠ <c>Grace</c> — теж відкриття: після простою задача станів проходить
    /// <c>Scheduled → Open → Grace</c> за один прогін). <c>Scheduled → … → Closed</c> — ні: у закритий період перерахунок
    /// не пише (<c>RecalculationWritePolicy</c>).
    /// </summary>
    /// <param name="before">Стан до.</param>
    /// <param name="after">Стан після.</param>
    public static bool Requires(PeriodState before, PeriodState after)
        => before == PeriodState.Scheduled && after is PeriodState.Open or PeriodState.Grace;
}
