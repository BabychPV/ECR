// src/Ecr.Worker/WorkerConfigurationValidation.cs

using System.Globalization;
using Ecr.Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace Ecr.Worker;

/// <summary>
/// Перевірка на старті воркера ключів, які дочірній процес перерахунку справді читає
/// (<c>AddEcrInfrastructure</c>, <c>AddEcrCalculations</c>): недійсне значення зупиняє службу з
/// ім'ям ключа (код <see cref="WorkerProgram.ExitInvalidConfiguration"/>), а не мовчки стає дефолтом.
/// </summary>
/// <remarks>
/// ⛔ R5-U1/U1-07 (аудит 2026-10-09): перевірка U19 (<c>EcrConfigurationValidation</c>) живе в Api й
/// виконується лише там, а воркер (з I2-2 — типовий виконавець перерахунку) складає ту саму
/// інфраструктуру зі своєї конфігурації. Межі — ті самі, що в переліку Api (<c>Integers</c>/<c>Choices</c>);
/// сторож <c>WorkerConfigurationValidationTests</c> звіряє їх з <c>appsettings.json</c>.
/// Порожнє значення — «не задано», як і в Api.
/// </remarks>
internal static class WorkerConfigurationValidation
{
    /// <summary>Цілі ключі, які читає складання дочірнього, і найменше допустиме значення.</summary>
    internal static readonly IReadOnlyList<(string Key, int Min)> Integers =
    [
        ("Database:CommandTimeoutSeconds", 0),
        ("Database:BulkBatchSize", 1),
        ("Database:SheetLockTimeoutSeconds", 0),
        ("Database:MaxPoolSize", 1),
        ("Calculations:MaxParallelism", 1),
        ("Calculations:MaxInputCellsPerBinding", 1),
    ];

    /// <summary>Ключі з переліком допустимих значень (без урахування регістру).</summary>
    internal static readonly IReadOnlyList<(string Key, string[] Allowed)> Choices =
    [
        ("Database:EditionMode", Enum.GetNames<SqlEditionMode>()),
    ];

    /// <summary>Недійсні значення — по рядку на ключ; порожньо, якщо все гаразд.</summary>
    /// <param name="configuration">Зведена конфігурація воркера.</param>
    public static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var problems = new List<string>();
        foreach (var (key, min) in Integers)
        {
            if (Value(configuration, key) is { } text
                && (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < min))
            {
                problems.Add(Describe(key, text, string.Create(CultureInfo.InvariantCulture, $"ціле число не менше {min}")));
            }
        }

        foreach (var (key, allowed) in Choices)
        {
            if (Value(configuration, key) is { } text
                && !allowed.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(Describe(key, text, "одне з: " + string.Join(", ", allowed)));
            }
        }

        return problems;
    }

    private static string? Value(IConfiguration configuration, string key)
        => configuration[key] is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

    private static string Describe(string key, string value, string expected)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{key} = «{value}»: очікується {expected} (змінна оточення ECR_{key.Replace(":", "__", StringComparison.Ordinal)} або {SiteFileHint}).");

    private const string SiteFileHint = @"%ProgramData%\ECR\config\appsettings.Production.json";
}
