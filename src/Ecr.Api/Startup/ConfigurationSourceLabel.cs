using Ecr.Application.Health;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace Ecr.Api.Startup;

/// <summary>
/// B105: мітка джерела, з якого прийшло дієве значення ключа конфігурації, — для <c>GET /api/v1/health/facts</c>.
/// </summary>
/// <remarks>
/// ⛔ Лише мітка, без шляху файлу чи імені змінної: відповідь потрапляє в скриншоти й тікети
/// (<see cref="Ecr.Application.Ports.ISystemHealthStore"/>). Різниці «змінна оточення / файл майданчика» досить, щоб
/// побачити, чи переживе значення перезапуск без скрипту вікна.
/// </remarks>
public static class ConfigurationSourceLabel
{
    /// <summary>Змінна оточення (<c>ECR_…</c>): живе лише доти, доки її задає той, хто запускає процес.</summary>
    public const string Environment = "Environment";

    /// <summary>Файл майданчика <c>%ProgramData%\ECR\config\appsettings.Production.json</c>: переживає перезапуск.</summary>
    public const string ProgramData = "ProgramData";

    /// <summary>Файл <c>appsettings*.json</c> поруч зі збіркою.</summary>
    public const string AppSettings = "AppSettings";

    /// <summary>Аргумент командного рядка.</summary>
    public const string CommandLine = "CommandLine";

    /// <summary>Інше джерело (пам'ять хоста, тестова фабрика тощо).</summary>
    public const string Other = "Other";

    /// <summary>Мітка останнього (дієвого) джерела, що задає ключ; <see cref="SystemHostFacts.SourceDefault"/> — ніхто.</summary>
    /// <param name="configuration">Конфігурація застосунку.</param>
    /// <param name="key">Ключ.</param>
    public static string Of(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration is not IConfigurationRoot root)
        {
            return configuration[key] is null ? SystemHostFacts.SourceDefault : Other;
        }

        // Пізніше додане джерело перекриває раніше — шукаємо з кінця, як і сам IConfigurationRoot.
        foreach (var provider in root.Providers.Reverse())
        {
            if (provider.TryGet(key, out _))
            {
                return LabelOf(provider);
            }
        }

        return SystemHostFacts.SourceDefault;
    }

    private static string LabelOf(IConfigurationProvider provider)
    {
        if (ProgramDataConfiguration.IsProgramDataProvider(provider))
        {
            return ProgramData;
        }

        return provider switch
        {
            EnvironmentVariablesConfigurationProvider => Environment,
            CommandLineConfigurationProvider => CommandLine,
            FileConfigurationProvider => AppSettings,
            _ => Other,
        };
    }
}
