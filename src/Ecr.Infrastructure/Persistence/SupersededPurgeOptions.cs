// src/Ecr.Infrastructure/Persistence/SupersededPurgeOptions.cs
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Налаштування прибирання прогонів, що перестали бути актуальними: результати, кроки, входи І сам рядок
/// <c>calc.CalculationRun</c> (Q-17, рішення людини 28.10; <c>D-560</c> / PSS-01, рішення людини 20.11 — виняток із
/// ЗБР-1/<c>D-25</c>: для «документ × період» лишається лише ОСТАННІЙ ланцюг — актуальний прогін).
/// </summary>
/// <param name="Enabled">Чи прибирати взагалі: швидкий шлях після перемикання прогону й нічний прохід.</param>
/// <param name="DeleteEnabled">
/// Чи нічний прохід ВИДАЛЯЄ (інакше лише лічить і пише в журнал). Швидкий шлях цього ключа не читає — він прибирає
/// лише прогони, які щойно замінило перемикання.
/// </param>
/// <param name="BatchSize">Скільки рядків видаляє один оператор <c>DELETE</c> (500: ≈ 5 замків на рядок &lt; 5000 порога ескалації).</param>
/// <param name="PauseBetweenBatches">Пауза між пакетами, щоб не тримати таблицю, яку читають і пишуть перерахунки.</param>
/// <param name="MaxBatchesPerRun">
/// Нічний бюджет: стеля пакетів, які нічна задача видаляє за ОДНЕ нічне вікно (накопичене дочищується за кілька ночей).
/// Швидкий шлях має власну стелю <see cref="FastPathMaxBatches"/>.
/// </param>
/// <param name="IncludeTrace">
/// Чи прибирати також <c>calc.CalculationStep</c> і <c>calc.CalculationInput</c> (ті самі правила, що для результатів).
/// <c>false</c> — рядок прогону лишається: на нього посилаються кроки й входи (FK).
/// </param>
/// <param name="LogPauseThresholdPercent">FULL recovery: пауза, поки використано більше цього відсотка журналу.</param>
/// <param name="LogPauseSeconds">Скільки секунд чекати між перевірками журналу.</param>
/// <param name="MaxLogPauses">Скільки разів чекати, перш ніж зупинити прохід (наступний продовжить).</param>
/// <param name="CheckpointEveryBatches">SIMPLE recovery: <c>CHECKPOINT</c> після кожних N пакетів.</param>
/// <param name="RebuildAfter">Чи перебудувати індекси зачеплених партицій і оновити статистику після повного прибирання.</param>
/// <remarks>
/// ⚠ Ключі читаються ЛІТЕРАЛАМИ (сторож <c>ConfigurationKeysTests</c> шукає повний рядок ключа з
/// <c>appsettings.json</c> у джерелах).
/// <para>
/// ✎ <c>D-560</c> (PSS-01, рішення людини 20.11): прибирання й видалення УВІМКНЕНІ за замовчуванням — і у файлі, і в
/// коді (ключ, якого немає, — увімкнено): служба <c>EcrWorker</c> <c>appsettings.json</c> Api не читає, а саме вона в
/// режимі <c>Database</c> завершує перерахунки й може виконати нічний прохід. Вимкнути — явним <c>false</c>.
/// <see cref="Read"/> без конфігурації (<c>null</c>, ручне створення сховища в тестах) — <see cref="Off"/>.
/// </para>
/// </remarks>
public sealed record SupersededPurgeOptions(
    bool Enabled,
    bool DeleteEnabled,
    int BatchSize,
    TimeSpan PauseBetweenBatches,
    int MaxBatchesPerRun,
    bool IncludeTrace,
    int LogPauseThresholdPercent,
    int LogPauseSeconds,
    int MaxLogPauses,
    int CheckpointEveryBatches,
    bool RebuildAfter)
{
    /// <summary>
    /// Нічне вікно (місцевий час сервера), лише в якому нічна задача ВИДАЛЯЄ накопичене; поза ним — лише лічить.
    /// Початок; кінець — <see cref="NightWindowEnd"/> (вікно може переходити через північ).
    /// </summary>
    public TimeSpan NightWindowStart { get; init; } = DefaultNightWindowStart;

    /// <summary>Кінець нічного вікна (місцевий час сервера); прохід, що до нього не доїв, зупиняється (<c>windowEnd</c>).</summary>
    public TimeSpan NightWindowEnd { get; init; } = DefaultNightWindowEnd;

    /// <summary>Ключ: нічне вікно видалення, <c>HH:mm-HH:mm</c> місцевого часу сервера.</summary>
    public const string NightWindowKey = "Calculations:SupersededPurge:NightWindow";

    /// <summary>Дефолт початку нічного вікна — 01:00.</summary>
    public static readonly TimeSpan DefaultNightWindowStart = TimeSpan.FromHours(1);

    /// <summary>Дефолт кінця нічного вікна — 05:00.</summary>
    public static readonly TimeSpan DefaultNightWindowEnd = TimeSpan.FromHours(5);

    /// <summary>
    /// Стеля пакетів ШВИДКОГО шляху (одне перемикання): він прибирає лише щойно витіснені прогони — одиниці, рідко
    /// сотні пакетів; накопиченого (backlog) не чіпає ніколи, його дочищує нічна задача у своєму вікні.
    /// </summary>
    public const int FastPathMaxBatches = 2000;

    /// <summary>Чи момент <paramref name="localTime"/> (місцевий час сервера) — усередині нічного вікна.</summary>
    /// <param name="localTime">Час доби.</param>
    public bool InNightWindow(TimeSpan localTime)
        => NightWindowStart <= NightWindowEnd
            ? localTime >= NightWindowStart && localTime < NightWindowEnd
            : localTime >= NightWindowStart || localTime < NightWindowEnd;

    /// <summary>
    /// Межі поточного входження нічного вікна (місцевий час), якщо <paramref name="local"/> у ньому; інакше <c>null</c>.
    /// </summary>
    /// <param name="local">Місцевий час сервера.</param>
    public (DateTime Start, DateTime End)? NightWindowAround(DateTime local)
    {
        if (!InNightWindow(local.TimeOfDay))
        {
            return null;
        }

        var start = local.Date + NightWindowStart;
        if (start > local)
        {
            start = start.AddDays(-1);
        }

        var end = start.Date + NightWindowEnd;
        if (end <= start)
        {
            end = end.AddDays(1);
        }

        return (start, end);
    }

    /// <summary>Розбирає <c>HH:mm-HH:mm</c>; недійсне — <c>null</c>.</summary>
    /// <param name="text">Значення ключа.</param>
    public static (TimeSpan Start, TimeSpan End)? ParseNightWindow(string? text)
    {
        var parts = text?.Split('-', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 }
            || !TimeSpan.TryParseExact(parts[0], @"hh\:mm", CultureInfo.InvariantCulture, out var start)
            || !TimeSpan.TryParseExact(parts[1], @"hh\:mm", CultureInfo.InvariantCulture, out var end)
            || start == end
            || start >= TimeSpan.FromDays(1)
            || end >= TimeSpan.FromDays(1))
        {
            return null;
        }

        return (start, end);
    }

    /// <summary>Ключ: чи прибирати взагалі.</summary>
    public const string EnabledKey = "Calculations:SupersededPurge:Enabled";

    /// <summary>Ключ: чи нічний прохід видаляє (інакше лише рахує).</summary>
    public const string DeleteEnabledKey = "Calculations:SupersededPurge:DeleteEnabled";

    /// <summary>Ключ: розмір пакета.</summary>
    public const string BatchSizeKey = "Calculations:SupersededPurge:BatchSize";

    /// <summary>Ключ: пауза між пакетами, мілісекунди.</summary>
    public const string PauseMillisecondsKey = "Calculations:SupersededPurge:PauseMilliseconds";

    /// <summary>Ключ: стеля пакетів за прохід.</summary>
    public const string MaxBatchesPerRunKey = "Calculations:SupersededPurge:MaxBatchesPerRun";

    /// <summary>Ключ: чи прибирати кроки й входи.</summary>
    public const string IncludeTraceKey = "Calculations:SupersededPurge:IncludeTrace";

    /// <summary>Ключ: поріг журналу (%) для паузи при FULL recovery.</summary>
    public const string LogPauseThresholdPercentKey = "Calculations:SupersededPurge:LogPauseThresholdPercent";

    /// <summary>Ключ: пауза між перевірками журналу, секунди.</summary>
    public const string LogPauseSecondsKey = "Calculations:SupersededPurge:LogPauseSeconds";

    /// <summary>Ключ: скільки пауз журналу до зупинки проходу.</summary>
    public const string MaxLogPausesKey = "Calculations:SupersededPurge:MaxLogPauses";

    /// <summary>Ключ: CHECKPOINT кожні N пакетів (SIMPLE).</summary>
    public const string CheckpointEveryBatchesKey = "Calculations:SupersededPurge:CheckpointEveryBatches";

    /// <summary>Ключ: перебудова індексів і статистика після прибирання.</summary>
    public const string RebuildAfterKey = "Calculations:SupersededPurge:RebuildAfter";

    /// <summary>Дефолт розміру пакета.</summary>
    public const int DefaultBatchSize = 500;

    /// <summary>Дефолт паузи між пакетами, мс.</summary>
    public const int DefaultPauseMilliseconds = 50;

    /// <summary>
    /// Дефолт нічного бюджету пакетів. ✎ <c>D-560</c>: накопичене (стенд Land — ≈ 36 тис. пакетів по 500) дочищується
    /// за кілька ночей; 5 000 пакетів ≈ 30–40 хв на кроках трейсу (замір 20.11) — гарантовано в межах вікна.
    /// </summary>
    public const int DefaultMaxBatchesPerRun = 5000;

    /// <summary>
    /// Скільки має минути від СТАРТУ <c>Failed</c>-прогону, перш ніж його можна прибрати (<c>D-560</c>).
    /// </summary>
    /// <remarks>
    /// <c>AbandonedWorkSweeper</c> закриває «покинутий» <c>Running</c> за 2 год, поки живі інші перерахунки, а довгий
    /// перерахунок документа-року живе й довше. Актуальним такий прогін не стане (<c>SwitchCurrentRunAsync</c>
    /// перечитує статус під <c>UPDLOCK</c> і відмовляє не-<c>Running</c>); доба — додаткова страховка, щоб не забирати
    /// рядки в ще живого прогону.
    /// </remarks>
    public static readonly TimeSpan FailedRunGrace = TimeSpan.FromHours(24);

    /// <summary>Дефолт порога журналу, %.</summary>
    public const int DefaultLogPauseThresholdPercent = 60;

    /// <summary>Дефолт паузи між перевірками журналу, с.</summary>
    public const int DefaultLogPauseSeconds = 30;

    /// <summary>Дефолт кількості пауз журналу.</summary>
    public const int DefaultMaxLogPauses = 20;

    /// <summary>Дефолт частоти CHECKPOINT.</summary>
    public const int DefaultCheckpointEveryBatches = 50;

    /// <summary>Прибирання вимкнено: явне <c>Enabled=false</c> або сховище без конфігурації (тести).</summary>
    public static SupersededPurgeOptions Off { get; } = new(
        Enabled: false,
        DeleteEnabled: false,
        BatchSize: DefaultBatchSize,
        PauseBetweenBatches: TimeSpan.FromMilliseconds(DefaultPauseMilliseconds),
        MaxBatchesPerRun: DefaultMaxBatchesPerRun,
        IncludeTrace: true,
        LogPauseThresholdPercent: DefaultLogPauseThresholdPercent,
        LogPauseSeconds: DefaultLogPauseSeconds,
        MaxLogPauses: DefaultMaxLogPauses,
        CheckpointEveryBatches: DefaultCheckpointEveryBatches,
        RebuildAfter: false);

    /// <summary>Читає налаштування; недійсне або порожнє значення — дефолт, нижні межі діють завжди.</summary>
    /// <param name="configuration">Конфігурація процесу; <c>null</c> — вимкнено.</param>
    public static SupersededPurgeOptions Read(IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return Off;
        }

        return new SupersededPurgeOptions(
            // ✎ D-560: ключа немає — увімкнено (див. remarks типу).
            Enabled: Flag(configuration, EnabledKey, defaultValue: true),
            DeleteEnabled: Flag(configuration, DeleteEnabledKey, defaultValue: true),
            BatchSize: Math.Max(1, Number(configuration, BatchSizeKey, DefaultBatchSize)),
            PauseBetweenBatches: TimeSpan.FromMilliseconds(
                Math.Max(0, Number(configuration, PauseMillisecondsKey, DefaultPauseMilliseconds))),
            MaxBatchesPerRun: Math.Max(1, Number(configuration, MaxBatchesPerRunKey, DefaultMaxBatchesPerRun)),
            IncludeTrace: Flag(configuration, IncludeTraceKey, defaultValue: true),
            LogPauseThresholdPercent: Math.Clamp(
                Number(configuration, LogPauseThresholdPercentKey, DefaultLogPauseThresholdPercent), 1, 100),
            LogPauseSeconds: Math.Max(0, Number(configuration, LogPauseSecondsKey, DefaultLogPauseSeconds)),
            MaxLogPauses: Math.Max(0, Number(configuration, MaxLogPausesKey, DefaultMaxLogPauses)),
            CheckpointEveryBatches: Math.Max(
                1, Number(configuration, CheckpointEveryBatchesKey, DefaultCheckpointEveryBatches)),
            RebuildAfter: Flag(configuration, RebuildAfterKey, defaultValue: false))
        {
            // Недійсне вікно — дефолт 01:00-05:00 (задача пише попередження в журнал, NightWindowInvalid).
            NightWindowStart = ParseNightWindow(configuration[NightWindowKey])?.Start ?? DefaultNightWindowStart,
            NightWindowEnd = ParseNightWindow(configuration[NightWindowKey])?.End ?? DefaultNightWindowEnd,
            NightWindowInvalid = !string.IsNullOrWhiteSpace(configuration[NightWindowKey])
                                 && ParseNightWindow(configuration[NightWindowKey]) is null,
        };
    }

    /// <summary>Значення <see cref="NightWindowKey"/> задано, але не розібрано (діє дефолт).</summary>
    public bool NightWindowInvalid { get; init; }

    private static bool Flag(IConfiguration configuration, string key, bool defaultValue)
        => bool.TryParse(configuration[key]?.Trim(), out var parsed) ? parsed : defaultValue;

    private static int Number(IConfiguration configuration, string key, int defaultValue)
        => int.TryParse(configuration[key]?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
}
