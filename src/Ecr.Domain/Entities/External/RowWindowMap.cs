// src/Ecr.Domain/Entities/External/RowWindowMap.cs
using System.Globalization;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Спосіб згортки вікна рядка (HSE301 §4.3–4.4, <c>D-171</c>).
/// </summary>
/// <remarks>
/// ⚠ Числа — ті самі, що в <c>Ecr.Application.Ports.SourceSummaryKind</c>:
/// у базі це один <c>tinyint</c>, і задача підтягування (A1) перекладає його в
/// запит порту прямим приведенням. Домен на Application посилатися не може,
/// тому перелік тут власний; розбіжність ловить
/// <c>RowWindowSchemaTests.Перелік_згортки_збігається_з_портом_джерела</c>.
/// </remarks>
public enum RowWindowSummaryKind : byte
{
    /// <summary>Інтеграл за часом, «одиниця × секунда» (аналог PI Total).</summary>
    Total = 0,

    /// <summary>Середнє, зважене за часом, по покритому часу.</summary>
    Average = 1,

    /// <summary>Найменше з придатних точок вікна.</summary>
    Minimum = 2,

    /// <summary>Найбільше з придатних точок вікна.</summary>
    Maximum = 3,

    /// <summary>Кількість придатних точок вікна.</summary>
    Count = 4,
}

/// <summary>
/// Прив'язка «атрибут джерела → колонка, вікно = рядок» (<c>ext.RowWindowMap</c>,
/// HSE301 §4.4, рішення V-2 → <c>D-171</c>).
/// </summary>
/// <remarks>
/// ⛔ Окрема сутність, а не ще один режим <see cref="EntityFieldMap"/>: той має
/// фіксованого адресата рядка (<c>EntityFieldMap.TargetRowKey</c>) і
/// <c>UQ_EntityFieldMap(SourceEntityId, SourceField)</c> — тобто тег, який дає
/// місячну суму, не зміг би водночас давати об'єм кожної події. Тут навпаки:
/// ОДИН атрибут обслуговує БАГАТО рядків, а вікно береться з самого рядка —
/// з колонок <see cref="StartColumnDefId"/> і <see cref="EndColumnDefId"/>.
///
/// ⚠ Прив'язка — «шаблонна» частина (що з чим і як). Звідки брати атрибут
/// для конкретного рядка, каже <see cref="Sources"/>: значення колонки-селектора
/// → атрибут; джерело з <c>SelectorValue = null</c> діє для всіх рядків.
/// </remarks>
public sealed class RowWindowMap : Entity<int>
{
    /// <summary>Типовий поріг покриття, нижче якого значення — <c>Partial</c> (§4.4).</summary>
    public const decimal DefaultMinPercentGood = 95m;

    /// <summary>Скільки діб повторювати підтягування за пізніми даними PI (§4.4).</summary>
    public const int DefaultRefetchWithinDays = 7;

    /// <summary>
    /// Стеля <see cref="RefetchWithinDays"/>: рік. Довше повторювати немає сенсу —
    /// період за цей час закривається й подається.
    /// </summary>
    public const int MaxRefetchWithinDays = 366;

    /// <summary>Довжина значення селектора — як <c>doc.TableRow.RowKey</c>.</summary>
    public const int MaxSelectorValueLength = 100;

    /// <summary>Довжина шляху атрибута — як <c>ext.EntityFieldMap.SourceField</c>.</summary>
    public const int MaxSourceFieldLength = 200;

    private readonly List<RowWindowSource> _sources = [];

    private RowWindowMap() { }

    private RowWindowMap(
        int tableDefId,
        int targetColumnDefId,
        int startColumnDefId,
        int endColumnDefId,
        int? selectorColumnDefId,
        RowWindowSummaryKind summary,
        bool isStep,
        int targetUnitId)
    {
        TableDefId = tableDefId;
        TargetColumnDefId = targetColumnDefId;
        StartColumnDefId = startColumnDefId;
        EndColumnDefId = endColumnDefId;
        SelectorColumnDefId = selectorColumnDefId;
        Summary = summary;
        IsStep = isStep;
        TargetUnitId = targetUnitId;
        MinPercentGood = DefaultMinPercentGood;
        RefetchWithinDays = DefaultRefetchWithinDays;
        IsActive = true;
    }

    /// <summary>Створює прив'язку, перевіривши колонки.</summary>
    /// <param name="target">Колонка-ціль: сюди лягає згорнуте значення; тип <c>Decimal</c>.</param>
    /// <param name="start">Колонка початку вікна; тип <c>Date</c>.</param>
    /// <param name="end">Колонка кінця вікна (виключно); тип <c>Date</c>.</param>
    /// <param name="selector">
    /// Колонка, значення якої обирає атрибут (<c>PiSourceKey</c>); <c>null</c> —
    /// один атрибут на всі рядки.
    /// </param>
    /// <param name="summary">Спосіб згортки.</param>
    /// <param name="isStep">Ряд ступінчастий (значення тримається до наступної точки).</param>
    /// <param name="targetUnitId">Одиниця, у якій значення лягає в колонку.</param>
    /// <returns>Нова прив'язка з типовими порогами (§4.4).</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c>: колонка вікна не <c>Date</c> (<c>.windowColumnsNotDate</c>),
    /// ціль не <c>Decimal</c> (<c>.targetNotDecimal</c>), колонка вікна з іншої
    /// таблиці (<c>.windowColumnNotInTable</c>), селектор з іншої таблиці
    /// (<c>.selectorNotInTable</c>), початок і кінець — та сама колонка
    /// (<c>.windowColumnsSame</c>).
    /// </exception>
    /// <remarks>
    /// ⚠ Перевірка тут, а не лише в обробнику API: тип колонки знає сам
    /// <see cref="ColumnDef"/>, і прив'язка на <c>String</c>-колонку як «початок
    /// вікна» не мала б жодного способу стати вікном — кожен рядок давав би
    /// <c>InvalidWindow</c> вже під час підтягування, тобто помилку конфігурації
    /// ловив би збір, а не налаштування (той самий принцип, що
    /// <c>EntityFieldMap.SetMaterialization</c>).
    ///
    /// ⚠ Таблиця прив'язки — таблиця цілі. Що колонки вікна й селектор належать
    /// їй же, тримає ще й база: зовнішні ключі складені, <c>(TableDefId, …)</c> на
    /// <c>UQ_ColumnDef_ForFk</c>, як у <c>FK_CellValue_Column</c>.
    /// </remarks>
    public static RowWindowMap Create(
        ColumnDef target,
        ColumnDef start,
        ColumnDef end,
        ColumnDef? selector,
        RowWindowSummaryKind summary,
        bool isStep,
        int targetUnitId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);

        if (!Enum.IsDefined(summary))
        {
            throw new ArgumentOutOfRangeException(nameof(summary), summary, "Невідомий спосіб згортки вікна.");
        }

        if (start.DataType != CellDataType.Date || end.DataType != CellDataType.Date)
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Вікно рядка задається колонками типу Date; «{start.Code}» має тип {start.DataType}, "
                + $"«{end.Code}» — {end.DataType}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.windowColumnsNotDate",
                    ["startColumn"] = start.Code,
                    ["endColumn"] = end.Code,
                });
        }

        if (target.DataType != CellDataType.Decimal)
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Згорнуте значення лягає лише в колонку типу Decimal; «{target.Code}» має тип {target.DataType}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.targetNotDecimal",
                    ["targetColumn"] = target.Code,
                    ["dataType"] = target.DataType.ToString(),
                });
        }

        foreach (var window in new[] { start, end })
        {
            if (window.TableDefId != target.TableDefId)
            {
                throw new DomainException(
                    "ECR-INT-0422",
                    $"Колонка вікна «{window.Code}» належить іншій таблиці, ніж ціль «{target.Code}».",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-INT-0422.windowColumnNotInTable",
                        ["column"] = window.Code,
                        ["targetColumn"] = target.Code,
                    });
            }
        }

        // Одна таблиця → код колонки унікальний, тож збіг кодів означає ту саму
        // колонку й тоді, коли Id ще не присвоєно (колонка не збережена).
        if (string.Equals(start.Code, end.Code, StringComparison.Ordinal))
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Початок і кінець вікна — та сама колонка «{start.Code}»: вікно завжди порожнє.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.windowColumnsSame",
                    ["column"] = start.Code,
                });
        }

        if (selector is not null && selector.TableDefId != target.TableDefId)
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Колонка-селектор «{selector.Code}» належить іншій таблиці, ніж ціль «{target.Code}».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.selectorNotInTable",
                    ["selectorColumn"] = selector.Code,
                    ["targetColumn"] = target.Code,
                });
        }

        return new RowWindowMap(
            target.TableDefId,
            target.Id,
            start.Id,
            end.Id,
            selector?.Id,
            summary,
            isStep,
            targetUnitId);
    }

    public int TableDefId { get; private set; }

    /// <summary>Колонка-ціль (<c>Decimal</c>); одна прив'язка на колонку (<c>UQ_RowWindowMap_Target</c>).</summary>
    public int TargetColumnDefId { get; private set; }

    /// <summary>Колонка початку вікна (<c>Date</c>, час проєкту), включно.</summary>
    public int StartColumnDefId { get; private set; }

    /// <summary>Колонка кінця вікна (<c>Date</c>, час проєкту), виключно.</summary>
    public int EndColumnDefId { get; private set; }

    /// <summary>Колонка-селектор атрибута; <c>null</c> — один атрибут на всі рядки.</summary>
    public int? SelectorColumnDefId { get; private set; }

    public RowWindowSummaryKind Summary { get; private set; }

    /// <summary>Форма ряду між точками (як «Step» атрибута AF; §4.1).</summary>
    public bool IsStep { get; private set; }

    /// <summary>
    /// Розрив між точками (секунди), довший за який відрізок — прогалина;
    /// <c>null</c> — порога немає (<c>WindowRequest.MaxGap</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Числа порогу в коді немає й не буде (§4.1): його знає той, хто
    /// налаштовує прив'язку, — частоту запису тегу. Секунди, а не <c>time</c>:
    /// <c>time</c> SQL Server не вміщає поріг, довший за добу.
    /// </remarks>
    public int? MaxGapSeconds { get; private set; }

    /// <summary>Поріг прогалини як <see cref="TimeSpan"/>; <c>null</c> — порога немає.</summary>
    public TimeSpan? MaxGap => MaxGapSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    public int TargetUnitId { get; private set; }

    /// <summary>Покриття (0–100), нижче якого значення має статус <c>Partial</c>.</summary>
    public decimal MinPercentGood { get; private set; }

    /// <summary>Скільки діб повторювати <c>NoData</c>/<c>Partial</c>/<c>SourceError</c>.</summary>
    public int RefetchWithinDays { get; private set; }

    public bool IsActive { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Значення селектора → атрибут джерела.</summary>
    public IReadOnlyList<RowWindowSource> Sources => _sources;

    /// <summary>Задає пороги підтягування.</summary>
    /// <param name="minPercentGood">Покриття 0–100, нижче якого — <c>Partial</c>.</param>
    /// <param name="refetchWithinDays">Діб повтору, 0–<see cref="MaxRefetchWithinDays"/>.</param>
    /// <param name="maxGap">Поріг прогалини, додатний; <c>null</c> — без порога.</param>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c> <c>.rowWindowPolicyOutOfRange</c> — значення поза межами.
    /// </exception>
    public void SetFetchPolicy(decimal minPercentGood, int refetchWithinDays, TimeSpan? maxGap)
    {
        if (minPercentGood is < 0m or > 100m || decimal.Round(minPercentGood, 2) != minPercentGood)
        {
            throw PolicyOutOfRange(nameof(MinPercentGood), minPercentGood);
        }

        if (refetchWithinDays is < 0 or > MaxRefetchWithinDays)
        {
            throw PolicyOutOfRange(nameof(RefetchWithinDays), refetchWithinDays);
        }

        int? maxGapSeconds = null;
        if (maxGap is { } gap)
        {
            // Цілі секунди: дробова частина не має сенсу для порогу розриву
            // між точками PI і не пройшла б у колонку int.
            if (gap <= TimeSpan.Zero || gap.TotalSeconds > int.MaxValue || gap.Ticks % TimeSpan.TicksPerSecond != 0)
            {
                throw PolicyOutOfRange(nameof(MaxGap), gap.TotalSeconds);
            }

            maxGapSeconds = (int)gap.TotalSeconds;
        }

        MinPercentGood = minPercentGood;
        RefetchWithinDays = refetchWithinDays;
        MaxGapSeconds = maxGapSeconds;
    }

    /// <summary>Додає джерело: значення селектора → атрибут.</summary>
    /// <param name="selectorValue">
    /// Значення колонки-селектора; <c>null</c> чи порожнє — джерело для всіх
    /// рядків, яким не знайшлося свого.
    /// </param>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourceField">Шлях атрибута.</param>
    /// <param name="sourceUnitId">Одиниця джерела (§4.2).</param>
    /// <returns>Додане джерело.</returns>
    /// <exception cref="DomainException">
    /// <c>ECR-INT-0422</c> <c>.rowWindowSelectorWithoutColumn</c> — значення
    /// селектора без колонки-селектора; <c>ECR-INT-0409</c>
    /// <c>.rowWindowSelectorTaken</c> — те саме значення вже має джерело.
    /// </exception>
    /// <remarks>
    /// ⛔ Дубль селектора — відмова, а не «останній виграє»: два атрибути на те
    /// саме значення означали б, що об'єм рядка залежить від порядку
    /// обходу, — правдоподібне число, походження якого не пояснити. Порівняння
    /// без урахування регістру — так само, як його порівнює
    /// <c>UQ_RowWindowSource</c> у базі (зіставлення <c>_CI_</c>).
    /// </remarks>
    public RowWindowSource AddSource(string? selectorValue, int sourceEntityId, string sourceField, int sourceUnitId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceField);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceField.Length, MaxSourceFieldLength, nameof(sourceField));

        var normalized = string.IsNullOrWhiteSpace(selectorValue) ? null : selectorValue.Trim();
        if (normalized is not null)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(normalized.Length, MaxSelectorValueLength, nameof(selectorValue));
        }

        if (normalized is not null && SelectorColumnDefId is null)
        {
            throw new DomainException(
                "ECR-INT-0422",
                $"Прив'язка не має колонки-селектора, тож значення «{normalized}» ніколи не збіглося б із рядком.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.rowWindowSelectorWithoutColumn",
                    ["selectorValue"] = normalized,
                });
        }

        if (_sources.Exists(s => string.Equals(s.SelectorValue, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainException(
                "ECR-INT-0409",
                normalized is null
                    ? "Прив'язка вже має джерело для всіх рядків."
                    : $"Значення селектора «{normalized}» уже має джерело в цій прив'язці.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.rowWindowSelectorTaken",
                    ["selectorValue"] = normalized,
                });
        }

        var source = new RowWindowSource(Id, normalized, sourceEntityId, sourceField, sourceUnitId);
        _sources.Add(source);
        return source;
    }

    // ⚠ Значення — рядком: резолвер каталогу підставляє лише string, і число
    // в Details лишило б користувачеві фігурні дужки замість себе.
    private static DomainException PolicyOutOfRange(string parameter, IFormattable value)
    {
        var text = value.ToString(null, CultureInfo.InvariantCulture);

        return new(
            "ECR-INT-0422",
            $"Параметр прив'язки {parameter} = {text} поза допустимими межами.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0422.rowWindowPolicyOutOfRange",
                ["parameter"] = parameter,
                ["value"] = text,
            });
    }
}

/// <summary>
/// Значення селектора → атрибут джерела (<c>ext.RowWindowSource</c>, HSE301 §4.4).
/// </summary>
public sealed class RowWindowSource : Entity<int>
{
    private RowWindowSource() { }

    internal RowWindowSource(int rowWindowMapId, string? selectorValue, int sourceEntityId, string sourceField, int sourceUnitId)
    {
        RowWindowMapId = rowWindowMapId;
        SelectorValue = selectorValue;
        SourceEntityId = sourceEntityId;
        SourceField = sourceField;
        SourceUnitId = sourceUnitId;
    }

    public int RowWindowMapId { get; private set; }

    /// <summary>Значення колонки-селектора; <c>null</c> — для всіх рядків.</summary>
    public string? SelectorValue { get; private set; }

    public int SourceEntityId { get; private set; }

    /// <summary>Шлях атрибута в джерелі.</summary>
    public string SourceField { get; private set; } = null!;

    /// <summary>Одиниця, у якій віддає значення джерело (§4.2).</summary>
    public int SourceUnitId { get; private set; }
}
