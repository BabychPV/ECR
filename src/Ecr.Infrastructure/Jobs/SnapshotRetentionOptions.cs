// src/Ecr.Infrastructure/Jobs/SnapshotRetentionOptions.cs
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Налаштування прибирання старих зрізів подання <c>calc.SubmissionSnapshot</c> (AN-134).
/// </summary>
/// <param name="Enabled">Чи виконувати прохід узагалі (без видалення він лише лічить і пише в журнал).</param>
/// <param name="DeleteEnabled">
/// Чи ВИДАЛЯТИ. Окремий ключ від <paramref name="Enabled"/>, за замовчуванням <c>false</c>: перший
/// прогін лише лічить (dry-run); видалення вмикається словом людини.
/// </param>
/// <param name="RetainSnapshots">Скільки НАЙНОВІШИХ зрізів лишається на «документ × аркуш × період»; не менше <see cref="MinRetainSnapshots"/>.</param>
/// <param name="BatchSize">Скільки зрізів видаляє один оператор <c>DELETE</c> (рядок несе payload ~10 МБ).</param>
/// <param name="PauseBetweenBatches">Пауза між пакетами: не тримати таблицю, яку читає подання.</param>
/// <param name="MaxBatchesPerRun">Стеля пакетів за прогін; решту доїсть наступна ніч.</param>
/// <remarks>
/// ⚠ Ключі читаються ЛІТЕРАЛАМИ, а не складаються з префікса: сторож
/// <c>ConfigurationKeysTests</c> шукає в джерелах повний рядок ключа з <c>appsettings.json</c>.
/// <para>
/// ⛔ Нижня межа <see cref="MinRetainSnapshots"/> = 2 діє двічі: <c>EcrConfigurationValidation</c> зупиняє
/// старт Api на меншому значенні, а <see cref="Read"/> піднімає до неї значення, що прийшло повз
/// перевірку (воркер-процес), — «лишити 0 зрізів» неможливо за жодної конфігурації.
/// </para>
/// </remarks>
public sealed record SnapshotRetentionOptions(
    bool Enabled,
    bool DeleteEnabled,
    int RetainSnapshots,
    int BatchSize,
    TimeSpan PauseBetweenBatches,
    int MaxBatchesPerRun)
{
    /// <summary>Ключ: чи виконувати прохід.</summary>
    public const string EnabledKey = "Retention:SubmissionSnapshots:Enabled";

    /// <summary>Ключ: чи видаляти (інакше лише dry-run).</summary>
    public const string DeleteEnabledKey = "Retention:SubmissionSnapshots:DeleteEnabled";

    /// <summary>Ключ: скільки останніх зрізів лишати на «документ × аркуш × період».</summary>
    public const string RetainSnapshotsKey = "Retention:SubmissionSnapshots:RetainSnapshots";

    /// <summary>Ключ: розмір пакета видалення.</summary>
    public const string BatchSizeKey = "Retention:SubmissionSnapshots:BatchSize";

    /// <summary>Ключ: пауза між пакетами, мілісекунди.</summary>
    public const string PauseMillisecondsKey = "Retention:SubmissionSnapshots:PauseMilliseconds";

    /// <summary>Ключ: стеля пакетів за прогін.</summary>
    public const string MaxBatchesPerRunKey = "Retention:SubmissionSnapshots:MaxBatchesPerRun";

    /// <summary>Найменше допустиме число зрізів, що лишаються: останній і попередній.</summary>
    public const int MinRetainSnapshots = 2;

    /// <summary>Дефолт: п'ять останніх зрізів.</summary>
    public const int DefaultRetainSnapshots = 5;

    /// <summary>Дефолт розміру пакета: payload великий, тож пакет малий.</summary>
    public const int DefaultBatchSize = 50;

    /// <summary>Дефолт паузи між пакетами, мілісекунди.</summary>
    public const int DefaultPauseMilliseconds = 500;

    /// <summary>Дефолт стелі пакетів за прогін.</summary>
    public const int DefaultMaxBatchesPerRun = 200;

    /// <summary>Читає налаштування; недійсне або порожнє значення — дефолт, нижні межі діють завжди.</summary>
    /// <param name="configuration">Конфігурація процесу.</param>
    public static SnapshotRetentionOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new SnapshotRetentionOptions(
            Enabled: Flag(configuration, EnabledKey, defaultValue: true),
            DeleteEnabled: Flag(configuration, DeleteEnabledKey, defaultValue: false),
            RetainSnapshots: Math.Max(MinRetainSnapshots, Number(configuration, RetainSnapshotsKey, DefaultRetainSnapshots)),
            BatchSize: Math.Max(1, Number(configuration, BatchSizeKey, DefaultBatchSize)),
            PauseBetweenBatches: TimeSpan.FromMilliseconds(Math.Max(0, Number(configuration, PauseMillisecondsKey, DefaultPauseMilliseconds))),
            MaxBatchesPerRun: Math.Max(1, Number(configuration, MaxBatchesPerRunKey, DefaultMaxBatchesPerRun)));
    }

    private static bool Flag(IConfiguration configuration, string key, bool defaultValue)
        => bool.TryParse(configuration[key]?.Trim(), out var parsed) ? parsed : defaultValue;

    private static int Number(IConfiguration configuration, string key, int defaultValue)
        => int.TryParse(configuration[key]?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
}
