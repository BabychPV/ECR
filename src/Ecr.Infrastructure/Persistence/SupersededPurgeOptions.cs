// src/Ecr.Infrastructure/Persistence/SupersededPurgeOptions.cs
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Налаштування прибирання результатів, кроків і входів прогонів, що перестали бути актуальними
/// (Q-17, ADR «Deleting results of superseded runs», рішення людини 28.10).
/// </summary>
/// <param name="Enabled">Чи прибирати взагалі: швидкий шлях після перемикання прогону й нічний прохід.</param>
/// <param name="DeleteEnabled">
/// Чи нічний прохід ВИДАЛЯЄ. Окремий ключ, за замовчуванням <c>false</c>: прохід лише лічить і пише в журнал.
/// Швидкий шлях цього ключа не читає — він прибирає лише прогони, які щойно замінило перемикання.
/// </param>
/// <param name="BatchSize">Скільки рядків видаляє один оператор <c>DELETE</c> (500: ≈ 5 замків на рядок &lt; 5000 порога ескалації).</param>
/// <param name="PauseBetweenBatches">Пауза між пакетами, щоб не тримати таблицю, яку читають і пишуть перерахунки.</param>
/// <param name="MaxBatchesPerRun">Стеля пакетів за один прохід нічної задачі; решту доїсть наступний.</param>
/// <param name="IncludeTrace">Чи прибирати також <c>calc.CalculationStep</c> і <c>calc.CalculationInput</c> (ті самі правила, що для результатів).</param>
/// <param name="LogPauseThresholdPercent">FULL recovery: пауза, поки використано більше цього відсотка журналу.</param>
/// <param name="LogPauseSeconds">Скільки секунд чекати між перевірками журналу.</param>
/// <param name="MaxLogPauses">Скільки разів чекати, перш ніж зупинити прохід (наступний продовжить).</param>
/// <param name="CheckpointEveryBatches">SIMPLE recovery: <c>CHECKPOINT</c> після кожних N пакетів.</param>
/// <param name="RebuildAfter">Чи перебудувати індекси зачеплених партицій і оновити статистику після повного прибирання.</param>
/// <remarks>
/// ⚠ Ключі читаються ЛІТЕРАЛАМИ (сторож <c>ConfigurationKeysTests</c> шукає повний рядок ключа з
/// <c>appsettings.json</c> у джерелах). Видалення вимкнено за замовчуванням: це незворотна зміна даних, і без слова
/// людини, вікна та повного бекапу вона не вмикається (так само, як <c>Retention:SubmissionSnapshots</c>).
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

    /// <summary>Дефолт стелі пакетів за прохід (Land ≈ 7,9 тис. пакетів, EXTRAPOLATED).</summary>
    public const int DefaultMaxBatchesPerRun = 2000;

    /// <summary>Дефолт порога журналу, %.</summary>
    public const int DefaultLogPauseThresholdPercent = 60;

    /// <summary>Дефолт паузи між перевірками журналу, с.</summary>
    public const int DefaultLogPauseSeconds = 30;

    /// <summary>Дефолт кількості пауз журналу.</summary>
    public const int DefaultMaxLogPauses = 20;

    /// <summary>Дефолт частоти CHECKPOINT.</summary>
    public const int DefaultCheckpointEveryBatches = 50;

    /// <summary>Прибирання вимкнено (значення, що лишається у виробничому файлі за замовчуванням).</summary>
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
            Enabled: Flag(configuration, EnabledKey, defaultValue: false),
            DeleteEnabled: Flag(configuration, DeleteEnabledKey, defaultValue: false),
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
            RebuildAfter: Flag(configuration, RebuildAfterKey, defaultValue: false));
    }

    private static bool Flag(IConfiguration configuration, string key, bool defaultValue)
        => bool.TryParse(configuration[key]?.Trim(), out var parsed) ? parsed : defaultValue;

    private static int Number(IConfiguration configuration, string key, int defaultValue)
        => int.TryParse(configuration[key]?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
}
