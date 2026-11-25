// src/Ecr.Application/GasComposition/GasCompositionContracts.cs
namespace Ecr.Application.GasComposition;

/// <summary>Права сторінки «Склад газу» (D-NEW).</summary>
public static class GasCompositionPermissions
{
    /// <summary>Перегляд потоків, кейсів і версій складу.</summary>
    public const string View = "GasComposition.View";

    /// <summary>Створення, зміна і видалення потоків і версій складу.</summary>
    public const string Edit = "GasComposition.Edit";
}

/// <summary>Потік у переліку.</summary>
/// <param name="Id">Запис потоку.</param>
/// <param name="StreamId">Id потоку (на нього посилаються події PI, FLERT <c>FlaringStream.ID</c>).</param>
/// <param name="Number">Номер.</param>
/// <param name="Name">Назва.</param>
/// <param name="IsOnshore">Суша.</param>
/// <param name="IsSour">Кислий.</param>
/// <param name="IsHmb">Потік HMB.</param>
/// <param name="Hidden">Прихований у списках.</param>
/// <param name="CaseCount">Кейсів.</param>
/// <param name="VersionCount">Версій усіх кейсів.</param>
/// <param name="LatestFrom">Найпізніша дата дії серед версій; <c>null</c> — версій немає.</param>
/// <param name="DeviatingVersions">Версій, у яких Σ поза 100 ± 0.5 (зокрема без складу).</param>
/// <param name="LowSumVersions">Версій, у яких 0 &lt; Σ &lt; 90 % (у FLERT — 7 кейсів): найімовірніше неповний склад.</param>
/// <param name="NoCompositionVersions">Версій без складу (Σ = 0, «No composition»; у FLERT — 16).</param>
public sealed record GasStreamListItemDto(
    long Id,
    int StreamId,
    string? Number,
    string Name,
    bool IsOnshore,
    bool IsSour,
    bool IsHmb,
    bool Hidden,
    int CaseCount,
    int VersionCount,
    DateOnly? LatestFrom,
    int DeviatingVersions,
    int LowSumVersions,
    int NoCompositionVersions);

/// <summary>Стан разового імпорту з FLERT.</summary>
/// <param name="Available">Імпорт можливий: у ECR ще немає жодного потоку.</param>
/// <param name="LastRun">Останній прогін імпорту; <c>null</c> — не було.</param>
public sealed record GasImportStatusDto(bool Available, Ports.FlertSyncRunDto? LastRun);

/// <summary>Перелік потоків.</summary>
/// <param name="Streams">Потоки.</param>
/// <param name="CanEdit">Чи має користувач право змінювати.</param>
/// <param name="CanImport">Чи може користувач запустити імпорт (право змін + керування інтеграціями).</param>
/// <param name="Import">Стан імпорту з FLERT.</param>
public sealed record GasStreamListDto(IReadOnlyList<GasStreamListItemDto> Streams, bool CanEdit, bool CanImport, GasImportStatusDto Import);

/// <summary>Компонент для редактора.</summary>
/// <param name="Code">Код.</param>
/// <param name="Name">Назва.</param>
/// <param name="Formula">Формула.</param>
/// <param name="Mw">Молярна маса.</param>
/// <param name="Group">Група для показу: <c>inert</c>, <c>hydrocarbon</c>, <c>aromatic</c>, <c>sulphur</c>, <c>other</c>.</param>
public sealed record GasComponentDto(string Code, string Name, string? Formula, decimal? Mw, string Group);

/// <summary>Замкнений період, що тримає версію.</summary>
/// <param name="Period">Підпис періоду (<c>2026-03</c> або <c>2026</c>).</param>
/// <param name="Reason"><c>closed</c> або <c>approved</c>.</param>
public sealed record GasLockDto(string Period, string Reason);

/// <summary>Версія кейсу.</summary>
/// <param name="Id">Запис версії.</param>
/// <param name="ValidFrom">Початок дії.</param>
/// <param name="ValidTo">Кінець (виключно); <c>null</c> — чинна.</param>
/// <param name="TemperatureC">Температура, °C.</param>
/// <param name="DensityOp">Робоча густина.</param>
/// <param name="MwHysys">Молярна маса HYSYS.</param>
/// <param name="Z">Стисливість.</param>
/// <param name="RuleParse">Правило FLERT (довідково).</param>
/// <param name="LegacyTypeId">Id кейсу FLERT.</param>
/// <param name="LegacyCompositionId">Id складу FLERT.</param>
/// <param name="FlertAliases">Значення довгих колонок-дублів FLERT, що розійшлися з коротким кодом (довідково).</param>
/// <param name="Sum">Σ мол.%.</param>
/// <param name="NoComposition">Складу немає.</param>
/// <param name="Components">Ненульові компоненти.</param>
/// <param name="Locks">Замкнені періоди всередині вікна: значення змінювати не можна.</param>
public sealed record GasVersionDto(
    long Id,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    decimal? TemperatureC,
    decimal? DensityOp,
    decimal? MwHysys,
    decimal? Z,
    string? RuleParse,
    int? LegacyTypeId,
    int? LegacyCompositionId,
    string? FlertAliases,
    decimal Sum,
    bool NoComposition,
    IReadOnlyList<GasComponentValue> Components,
    IReadOnlyList<GasLockDto> Locks);

/// <summary>Кейс потоку зі шкалою версій.</summary>
/// <param name="Name">Назва кейсу.</param>
/// <param name="Versions">Версії за датою дії.</param>
public sealed record GasCaseDto(string Name, IReadOnlyList<GasVersionDto> Versions);

/// <summary>Потік з кейсами.</summary>
/// <param name="Id">Запис потоку.</param>
/// <param name="StreamId">Id потоку.</param>
/// <param name="Number">Номер.</param>
/// <param name="Name">Назва.</param>
/// <param name="IsOnshore">Суша.</param>
/// <param name="IsSour">Кислий.</param>
/// <param name="IsHmb">Потік HMB.</param>
/// <param name="HmbDoc">Документ HMB.</param>
/// <param name="Group">Група.</param>
/// <param name="Hidden">Прихований у списках.</param>
/// <param name="Cases">Кейси.</param>
/// <param name="CanEdit">Право змін.</param>
public sealed record GasStreamDetailDto(
    long Id,
    int StreamId,
    string? Number,
    string Name,
    bool IsOnshore,
    bool IsSour,
    bool IsHmb,
    string? HmbDoc,
    int? Group,
    bool Hidden,
    IReadOnlyList<GasCaseDto> Cases,
    bool CanEdit);

/// <summary>Результат зміни: потік після неї і поставлений перерахунок.</summary>
/// <param name="Stream">Потік після зміни; <c>null</c> — потік видалено.</param>
/// <param name="QueuedYears">Роки, за які поставлено перерахунок залежних документів.</param>
/// <param name="FailedYears">Роки, за які поставити не вдалося (перерахуйте вручну).</param>
public sealed record GasChangeResultDto(GasStreamDetailDto? Stream, IReadOnlyList<int> QueuedYears, IReadOnlyList<int> FailedYears);

/// <summary>Атрибути потоку.</summary>
/// <param name="Name">Назва (унікальна разом із «суша/море»).</param>
/// <param name="Number">Номер.</param>
/// <param name="IsOnshore">Суша.</param>
/// <param name="IsSour">Кислий.</param>
/// <param name="IsHmb">Потік HMB.</param>
/// <param name="HmbDoc">Документ HMB.</param>
/// <param name="Group">Група.</param>
/// <param name="Hidden">Прихований у списках.</param>
public sealed record GasStreamRequest(
    string Name, string? Number, bool IsOnshore, bool IsSour, bool IsHmb, string? HmbDoc, int? Group, bool Hidden);

/// <summary>Нова версія кейсу (або перша версія нового кейсу).</summary>
/// <param name="CaseName">Кейс.</param>
/// <param name="ValidFrom">З якого місяця діє (1-ше число).</param>
/// <param name="TemperatureC">Температура.</param>
/// <param name="DensityOp">Густина.</param>
/// <param name="MwHysys">Молярна маса.</param>
/// <param name="Z">Стисливість.</param>
/// <param name="Components">Компоненти.</param>
/// <param name="AcceptSumDeviation">Підтвердження Σ поза 100 ± 0.5.</param>
public sealed record GasVersionCreateRequest(
    string CaseName,
    DateOnly ValidFrom,
    decimal? TemperatureC,
    decimal? DensityOp,
    decimal? MwHysys,
    decimal? Z,
    IReadOnlyList<GasComponentValue> Components,
    bool AcceptSumDeviation);

/// <summary>Зміна версії.</summary>
/// <param name="ValidFrom">З якого місяця діє.</param>
/// <param name="TemperatureC">Температура.</param>
/// <param name="DensityOp">Густина.</param>
/// <param name="MwHysys">Молярна маса.</param>
/// <param name="Z">Стисливість.</param>
/// <param name="Components">Компоненти (повний склад версії).</param>
/// <param name="AcceptSumDeviation">Підтвердження Σ поза 100 ± 0.5.</param>
public sealed record GasVersionUpdateRequest(
    DateOnly ValidFrom,
    decimal? TemperatureC,
    decimal? DensityOp,
    decimal? MwHysys,
    decimal? Z,
    IReadOnlyList<GasComponentValue> Components,
    bool AcceptSumDeviation);

/// <summary>Перейменування кейсу (усіх його версій).</summary>
/// <param name="From">Поточна назва.</param>
/// <param name="To">Нова назва.</param>
public sealed record GasCaseRenameRequest(string From, string To);
