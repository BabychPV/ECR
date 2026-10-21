using System.Runtime.CompilerServices;

namespace Ecr.Api.Tests;

/// <summary>
/// Вимикає inotify-стеження за файлами для всього тестового процесу. Кожна
/// <c>WebApplicationFactory</c> (EcrApiFactory, SecondNodeApiFactory, ProductionFactory, ...)
/// піднімає хост із <c>reloadOnChange</c> для appsettings, тобто з власним file watcher; на Linux-раннері
/// сотні фабрик в одному процесі вичерпують ліміт inotify instances (128) —
/// CI 39564415254: <c>IOException: user limit (128) on the number of inotify instances</c>.
/// Тести не використовують hot reload, тому стеження не потрібне; polling-watcher
/// лишається страховкою для будь-яких інших <c>PhysicalFileProvider</c>.
/// Initializer виконується до будь-якого коду тестів, тож покриває всі фабрики одним місцем.
/// </summary>
internal static class FileWatcherIsolation
{
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "1");
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
    }
}
