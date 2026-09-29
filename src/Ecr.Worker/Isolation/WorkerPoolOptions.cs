// src/Ecr.Worker/Isolation/WorkerPoolOptions.cs

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ecr.Worker.Isolation;

/// <summary>
/// Налаштування пулу <c>Jobs:Workers:*</c> (ФВ-9.8, D-206). Дефолти — цільовий
/// профіль сервера застосунку (24 ядра, 32 ГБ, SQL Server окремо).
/// </summary>
/// <remarks>
/// ⛔ Недійсне значення зупиняє старт з ім'ям ключа (як <c>U19</c> для
/// <c>Ecr.Api</c>), а не мовчки підміняється дефолтом: <c>MemoryLimitMb = 1</c>
/// дав би нескінченне коло «старт → OOM → рестарт».
///
/// ⚠ У P1 ключі читає лише <c>Ecr.Worker --supervisor</c> (файл
/// <c>worker.settings.json</c> поруч з exe + змінні <c>ECR_</c>). Коли наглядач
/// переїде в <c>Ecr.Api</c> (I1), ключі стануть частиною його
/// <c>appsettings.json</c> і <c>EcrConfigurationValidation</c>.
/// </remarks>
public sealed class WorkerPoolOptions
{
    /// <summary>Секція конфігурації.</summary>
    public const string SectionName = "Jobs:Workers";

    /// <summary>Найменша стеля процесу: нижче .NET-хост не стартує стабільно.</summary>
    public const int MinMemoryLimitMb = 64;

    /// <summary>Кількість дочірніх процесів.</summary>
    public int Count { get; init; } = 10;

    /// <summary>Стеля закоміченої пам'яті одного процесу, МБ.</summary>
    public int MemoryLimitMb { get; init; } = 2048;

    /// <summary>Стеля всього пулу, МБ (≈22 ГБ: лишає ~4 ГБ застосунку й ~3 ГБ ОС).</summary>
    public int JobMemoryLimitMb { get; init; } = 22528;

    /// <summary>Найдовша тривалість однієї задачі; застосовується з I1 (оренда черги).</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Читає й перевіряє секцію; порожнє значення — дефолт.</summary>
    /// <param name="configuration">Зведена конфігурація.</param>
    /// <param name="options">Прочитані налаштування (дефолти там, де є проблема).</param>
    /// <returns>Недійсні значення — по рядку на ключ; порожньо, якщо все гаразд.</returns>
    public static IReadOnlyList<string> TryLoad(IConfiguration configuration, out WorkerPoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        var defaults = new WorkerPoolOptions();
        var problems = new List<string>();

        var count = Integer(section, nameof(Count), defaults.Count, 1, problems);
        var memory = Integer(section, nameof(MemoryLimitMb), defaults.MemoryLimitMb, MinMemoryLimitMb, problems);
        var job = Integer(section, nameof(JobMemoryLimitMb), defaults.JobMemoryLimitMb, MinMemoryLimitMb, problems);
        if (job < memory)
        {
            problems.Add(Describe(
                nameof(JobMemoryLimitMb),
                job.ToString(CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"стеля пулу не менша за стелю процесу ({memory} МБ)")));
        }

        var duration = defaults.MaxDuration;
        if (Value(section, nameof(MaxDuration)) is { } text
            && (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out duration) || duration <= TimeSpan.Zero))
        {
            problems.Add(Describe(nameof(MaxDuration), text, "додатний інтервал hh:mm:ss"));
            duration = defaults.MaxDuration;
        }

        options = new WorkerPoolOptions
        {
            Count = count,
            MemoryLimitMb = memory,
            JobMemoryLimitMb = job,
            MaxDuration = duration,
        };
        return problems;
    }

    private static int Integer(IConfigurationSection section, string name, int fallback, int min, List<string> problems)
    {
        if (Value(section, name) is not { } text)
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < min)
        {
            problems.Add(Describe(name, text, string.Create(CultureInfo.InvariantCulture, $"ціле число не менше {min}")));
            return fallback;
        }

        return number;
    }

    private static string? Value(IConfigurationSection section, string name)
        => section[name] is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string Describe(string name, string value, string expected)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{SectionName}:{name} = «{value}»: очікується {expected} (змінна оточення ECR_Jobs__Workers__{name}).");
}
