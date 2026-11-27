// src/Ecr.Application/Ports/IGasCompositionStore.cs
using Ecr.Application.GasComposition;

namespace Ecr.Application.Ports;

/// <summary>Довідники складу газу й Id полів, які читає і пише сторінка «Склад газу».</summary>
/// <param name="Stream">Потоки (<c>STREAM</c>).</param>
/// <param name="Case">Версії кейсів (<c>STREAM_CASE</c>, темпоральний).</param>
/// <param name="Composition">Рядки складу (<c>GAS_COMPOSITION</c>).</param>
/// <param name="Component">Компоненти (<c>COMPONENT</c>).</param>
public sealed record GasRegistryIds(int Stream, int Case, int Composition, int Component)
{
    /// <summary>Усі чотири Id.</summary>
    public IReadOnlyList<int> All => [Stream, Case, Composition, Component];
}

/// <summary>Потік, як його бачить сторінка.</summary>
/// <param name="Id">Запис <c>STREAM</c>.</param>
/// <param name="StreamId">Id потоку (FLERT <c>FlaringStream.ID</c>; на нього посилаються події PI).</param>
/// <param name="Number">Номер.</param>
/// <param name="Name">Назва.</param>
/// <param name="IsOnshore">Суша.</param>
/// <param name="IsSour">Кислий.</param>
/// <param name="IsHmb">Потік HMB.</param>
/// <param name="HmbDoc">Документ HMB.</param>
/// <param name="Group">Група.</param>
/// <param name="Hidden">Прихований у списках.</param>
public sealed record GasStreamRecord(
    long Id, int StreamId, string? Number, string? Name, bool IsOnshore, bool IsSour, bool IsHmb, string? HmbDoc, int? Group, bool Hidden);

/// <summary>Версія кейсу з усіма значеннями.</summary>
/// <param name="Id">Запис <c>STREAM_CASE</c>.</param>
/// <param name="Code">Код запису.</param>
/// <param name="StreamEntryId">Потік.</param>
/// <param name="CaseName">Назва кейсу.</param>
/// <param name="ValidFrom">Початок дії.</param>
/// <param name="ValidTo">Кінець, виключно; <c>null</c> — чинна.</param>
/// <param name="TemperatureC">Температура.</param>
/// <param name="DensityOp">Густина.</param>
/// <param name="MwHysys">Молярна маса.</param>
/// <param name="Z">Стисливість.</param>
/// <param name="RuleParse">Правило FLERT.</param>
/// <param name="LegacyTypeId">Id кейсу FLERT.</param>
/// <param name="LegacyCompositionId">Id складу FLERT.</param>
/// <param name="FlertAliases">Значення довгих колонок-дублів FLERT, що розійшлися з коротким кодом.</param>
/// <param name="Rows">Рядки складу, зокрема вимкнені (для повторного ввімкнення).</param>
public sealed record GasVersionRecord(
    long Id,
    string Code,
    long StreamEntryId,
    string CaseName,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    decimal? TemperatureC,
    decimal? DensityOp,
    decimal? MwHysys,
    decimal? Z,
    string? RuleParse,
    int? LegacyTypeId,
    int? LegacyCompositionId,
    string? FlertAliases,
    IReadOnlyList<GasRowRecord> Rows)
{
    /// <summary>Слот шкали часу (порожній початок вважається найдавнішим).</summary>
    public GasVersionSlot Slot => new(Id, ValidFrom ?? DateOnly.MinValue, ValidTo);

    /// <summary>Ввімкнені рядки складу.</summary>
    public IEnumerable<GasRowRecord> ActiveRows => Rows.Where(r => r.IsActive);
}

/// <summary>Рядок складу версії.</summary>
/// <param name="Id">Запис <c>GAS_COMPOSITION</c>.</param>
/// <param name="Code">Код запису.</param>
/// <param name="Component">Код компонента.</param>
/// <param name="MolPct">Мол.%.</param>
/// <param name="IsActive">Увімкнений.</param>
public sealed record GasRowRecord(long Id, string Code, string Component, decimal MolPct, bool IsActive);

/// <summary>Компонент довідника <c>COMPONENT</c>.</summary>
/// <param name="Id">Запис.</param>
/// <param name="Code">Код.</param>
/// <param name="Names">Назва запису за мовами, без значень, рівних коду (такі — не назва, а заглушка імпорту).</param>
/// <param name="Formula">Хімічна формула.</param>
/// <param name="Mw">Молярна маса, г/моль.</param>
/// <param name="Ordinal">Порядок.</param>
public sealed record GasComponentRecord(
    long Id, string Code, IReadOnlyDictionary<string, string> Names, string? Formula, decimal? Mw, int Ordinal);

/// <summary>Читання складу газу для сторінки «Склад газу» (D-NEW).</summary>
public interface IGasCompositionStore
{
    /// <summary>Id довідників і їхні коди; <c>null</c> — довідників ще немає (нічого не імпортовано й не створено).</summary>
    /// <param name="ct">Скасування.</param>
    public Task<GasRegistryIds?> FindRegistriesAsync(CancellationToken ct);

    /// <summary>Живі потоки.</summary>
    /// <param name="ids">Довідники.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<GasStreamRecord>> ListStreamsAsync(GasRegistryIds ids, CancellationToken ct);

    /// <summary>Живі версії кейсів: усі або одного потоку; з рядками складу.</summary>
    /// <param name="ids">Довідники.</param>
    /// <param name="streamEntryId">Потік; <c>null</c> — усі (без рядків складу, лише Σ у <see cref="GasVersionRecord.Rows"/>).</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<GasVersionRecord>> ListVersionsAsync(GasRegistryIds ids, long? streamEntryId, CancellationToken ct);

    /// <summary>Компоненти.</summary>
    /// <param name="ids">Довідники.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<GasComponentRecord>> ListComponentsAsync(GasRegistryIds ids, CancellationToken ct);

    /// <summary>
    /// Замкнені періоди: актуальний прогін методології, що читає хоч один із довідників складу (<c>cfg.RegistryUse</c>), у
    /// закритому періоді або на затвердженому аркуші.
    /// </summary>
    /// <param name="ids">Довідники.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<GasLockedPeriod>> ListLockedPeriodsAsync(GasRegistryIds ids, CancellationToken ct);

    /// <summary>
    /// ✎ C4(a)/D4 (REVIEW-sg-flaring): те саме за довільними довідниками (<c>LP_SOUR_SPLIT</c>, <c>TOX_GAS_STREAM</c>,
    /// <c>FLARE_LOCATION</c>) — той самий зв'язок <c>cfg.RegistryUse.SourceId = CalculationResult.MethodologyVersionId</c>;
    /// вікно кожного періоду — з <c>doc.Period.PeriodStart/PeriodEnd</c>.
    /// </summary>
    /// <param name="registryDefIds">Довідники.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<GasLockedPeriod>> ListLockedPeriodsAsync(IReadOnlyCollection<int> registryDefIds, CancellationToken ct);

    /// <summary>Роки з відкритими/пільговими залежними документами для кожного довідника складу.</summary>
    /// <param name="ids">Довідники.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> ListDependentYearsAsync(GasRegistryIds ids, CancellationToken ct);

    /// <summary>Найбільший Id потоку серед УСІХ записів (зокрема видалених): Id не перевикористовується.</summary>
    /// <param name="ids">Довідники.</param>
    /// <param name="ct">Скасування.</param>
    public Task<int> MaxStreamIdAsync(GasRegistryIds ids, CancellationToken ct);

    /// <summary>Усі коди записів довідника (зокрема видалених): код за видаленим лишається зайнятим.</summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="prefix">Префікс коду.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<string>> ListCodesAsync(int registryDefId, string prefix, CancellationToken ct);
}

/// <summary>Заведення довідників складу газу (власник — ECR, <c>Local</c>), якщо їх ще немає.</summary>
public interface IGasCompositionProvisioning
{
    /// <summary>Заводить відсутні довідники й компоненти; наявні — перевіряє; дзеркало <c>External</c> перемикає на <c>Local</c>.</summary>
    /// <param name="ct">Скасування.</param>
    public Task<GasRegistryIds> EnsureAsync(CancellationToken ct);
}
