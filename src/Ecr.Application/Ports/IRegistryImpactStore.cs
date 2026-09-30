// src/Ecr.Application/Ports/IRegistryImpactStore.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання «які документи зачепила правка довідника» (RT-25, FEATURE-REGISTRY-TABLES §5.10).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не метод <see cref="IRegistryStore"/>: залежність документа від довідника
/// виводиться з чужих таблиць (<c>cfg.RegistryUse</c>, <c>calc.CalculationResult</c>, <c>doc.Period</c>),
/// і розширення <see cref="IRegistryStore"/> зачепило б кожну його реалізацію в тестах.
/// </remarks>
public interface IRegistryImpactStore
{
    /// <summary>Стеля вибірки: рядків «документ × період × методологія» за один виклик.</summary>
    public const int MaxRows = 1000;

    /// <summary>
    /// Документи ВІДКРИТИХ періодів (<c>Open</c>/<c>Grace</c>), чий актуальний прогін дав числа
    /// за версією методології, що читає довідник (<c>cfg.RegistryUse</c>, <c>SourceKind = 1</c>),
    /// і почався ДО останньої правки даних довідника (<c>DataChangedAt</c>): перерахований після правки
    /// документ уже свіжий і в перелік не входить.
    /// </summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="take">Скільки рядків узяти щонайбільше (не більше <see cref="MaxRows"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Рядки, впорядковані за документом і періодом; один рядок на методологію.</returns>
    /// <remarks>
    /// ⛔ Закриті періоди тут НЕ повертаються взагалі (<c>D-39</c>): їх не перераховують
    /// автоматично, і пропонувати їх у переліку означало б обіцяти дію, якої не буде.
    /// <see cref="PeriodState.Scheduled"/> теж поза переліком — чисел там ще немає.
    /// </remarks>
    public Task<IReadOnlyList<RegistryImpactRow>> ListImpactedAsync(int registryDefId, int take, CancellationToken ct);
}

/// <summary>Один зачеплений документ у періоді, через одну методологію.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="BusinessKey">Бізнес-ключ документа.</param>
/// <param name="ProjectId">Проєкт документа — для перевірки права рівня проєкту.</param>
/// <param name="PeriodKey">Період результатів.</param>
/// <param name="PeriodState">Стан періоду: <c>Open</c> або <c>Grace</c>.</param>
/// <param name="MethodologyCode">Код методології, версія якої читає довідник.</param>
public sealed record RegistryImpactRow(
    long DocumentId, string BusinessKey, int ProjectId, int PeriodKey, PeriodState PeriodState, string MethodologyCode);
