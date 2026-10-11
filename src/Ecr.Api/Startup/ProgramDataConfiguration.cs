using Microsoft.Extensions.Configuration.Json;

namespace Ecr.Api.Startup;

/// <summary>
/// Персистентна конфігурація майданчика з %ProgramData% (Q-213).
/// </summary>
/// <remarks>
/// Окремо від <c>Program.cs</c>, а не вбудований код там, — щоб можна було
/// перевірити (D-134) без підняття всього хоста: чи справді читається
/// саме той шлях, який кладе інсталятор (<c>Folders.wxs</c>: ConfigFolder,
/// <c>NeverOverwrite</c>), і чи справді ECR_-змінні оточення (D-11)
/// перекривають цей файл, а не навпаки — той самий порядок викликів, що
/// й тут, а не лише в описі.
/// </remarks>
public static partial class ProgramDataConfiguration
{
    /// <summary>
    /// Додає <c>%ProgramData%\ECR\config\appsettings.Production.json</c> як
    /// джерело конфігурації. <c>optional: true</c> — на dev/CI-машинах
    /// цього шляху немає взагалі, і це не помилка.
    /// </summary>
    /// <remarks>
    /// ⛔ СЕКРЕТИ (рядок підключення тощо) у цей файл ніколи не пишуться
    /// (D-11) — лише через змінні оточення служби
    /// (<c>tools/deploy-ecr.ps1 -ConnectionString</c>).
    /// </remarks>
    public static IConfigurationBuilder AddProgramDataConfig(
        this IConfigurationBuilder builder, string commonApplicationDataFolder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var path = Path.Combine(
            commonApplicationDataFolder, "ECR", "config", "appsettings.Production.json");

        // ⛔ U1-08: свій постачальник замість `AddJsonFile`. Стандартний `FileConfigurationProvider` на ПЕРЕЧИТУВАННІ
        // (файл змінено на живій службі) спершу СКИДАЄ усі ключі файлу, а потім розбирає його: синтаксична помилка
        // (зайва кома, незакрита дужка при ручному редагуванні) лишала службу без жодного значення з файлу —
        // дефолти коду замість налаштувань майданчика, без жодного рядка в журналі. Тепер невдале перечитування
        // залишає ОСТАННЄ ВДАЛЕ значення і лише повідомляє про помилку (`ReloadFailed`). Перше читання (старт)
        // поводиться як раніше: непридатний файл зупиняє службу.
        var source = new ResilientJsonConfigurationSource
        {
            Path = path,
            Optional = true,
            ReloadOnChange = true,
        };
        source.ResolveFileProvider();
        return builder.Add(source);
    }

    /// <summary>
    /// Куди звітувати про невдале перечитування файлу (U1-08). За замовчуванням — нікуди; <c>Program.cs</c> підключає
    /// журнал після побудови хоста (<see cref="AttachReloadLogger"/>).
    /// </summary>
    public static Action<string, Exception>? ReloadFailed { get; set; }

    /// <summary>Підключає журнал для звітів про невдале перечитування файлу конфігурації.</summary>
    /// <param name="logger">Журнал.</param>
    public static void AttachReloadLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        ReloadFailed = (file, exception) => LogReloadFailed(logger, exception, file);
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Файл конфігурації {File} змінено з помилкою: лишаються значення з останнього вдалого читання. "
                  + "Виправте файл — зміни підхопляться автоматично.")]
    private static partial void LogReloadFailed(ILogger logger, Exception exception, string file);

    private sealed class ResilientJsonConfigurationSource : JsonConfigurationSource
    {
        public override IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            EnsureDefaults(builder);
            return new ResilientJsonConfigurationProvider(this);
        }
    }

    private sealed class ResilientJsonConfigurationProvider(JsonConfigurationSource source) : JsonConfigurationProvider(source)
    {
        private Dictionary<string, string?>? _lastGood;

        public override void Load(Stream stream)
        {
            try
            {
                base.Load(stream);
                _lastGood = new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (_lastGood is not null && ex is not OperationCanceledException)
            {
                // `FileConfigurationProvider` на перечитуванні вже підмінив `Data` порожнім словником — повертаємо
                // останній вдалий знімок і не пробиваємо виняток у потік спостерігача за файлом.
                Data = new Dictionary<string, string?>(_lastGood, StringComparer.OrdinalIgnoreCase);
                ReloadFailed?.Invoke(Source.Path ?? string.Empty, ex);
            }
        }
    }
}
