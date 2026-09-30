using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;

namespace Ecr.Setup;

/// <summary>
/// Запускає <c>tools/deploy-ecr.ps1</c> В ОДНОМУ ПРОЦЕСІ через
/// Microsoft.PowerShell.SDK (<see cref="PowerShell"/>), а не як окремий
/// процес <c>pwsh.exe</c>/<c>powershell.exe</c>. Причина задокументована в
/// docs/build/11-install-guide.md (пошук "SecureString"): передача
/// <see cref="SecureString"/> новому процесу командного рядка ламається —
/// "Cannot convert ... to SecureString". В межах одного процесу
/// <see cref="SecureString"/>-об'єкт передається як є через
/// <see cref="PowerShell.AddParameter(string, object)"/>.
/// </summary>
internal sealed class DeployRunner
{
    /// <summary>Один рядок виводу скрипта (Information/Warning/Error) — для живого журналу кроку 6.</summary>
    public event Action<string>? OutputReceived;

    public async Task<bool> RunAsync(WizardState state, string scriptPath, CancellationToken ct)
    {
        // ⛔ Реальний прогін (людина): "cannot be loaded because running
        // scripts is disabled on this system" — виконання .ps1-ФАЙЛУ
        // підкоряється execution policy машини, НЕЗАЛЕЖНО від того, що
        // PowerShell хоститься в своєму процесі (те, що вирішує
        // SecureString-пастку вище, тут не рятує: обидва обмеження діють
        // одночасно й незалежно). §2.2 install-guide каже адміністратору
        // самому виконати `Set-ExecutionPolicy -Scope Process -Bypass`
        // ПЕРЕД викликом CLI — майстер, хостячи PowerShell сам, мусить
        // зробити те саме сам. `ExecutionPolicy.Bypass` на InitialSessionState
        // діє лише в межах ЦЬОГО процесу (EcrSetup.exe) і лише в пам'яті —
        // не чіпає ні реєстр, ні політику машини/користувача, і зникає
        // разом із процесом.
        var sessionState = InitialSessionState.CreateDefault();
        sessionState.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        using var ps = PowerShell.Create(sessionState);

        ps.AddCommand(scriptPath);

        // Перелік параметрів — у DeployArguments (чиста функція стану, під
        // тестом); тут лише передача. `null` — перемикач.
        foreach (var (name, value) in DeployArguments.Build(state))
        {
            if (value is null)
            {
                ps.AddParameter(name);
            }
            else
            {
                ps.AddParameter(name, value);
            }
        }

        // ⛔ ФВ-9.8 / D-206 (P2): MSI не пам'ятає WORKER_ENABLED. З I2-2
        // deploy-ecr.ps1 ставить воркер ТИПОВО (WORKER_ENABLED=1), крім SQL
        // Server Express, де без -EnableWorker іде WORKER_ENABLED=0 — тобто
        // оновлення через майстер на Express мовчки ПРИБРАЛО б уже
        // встановлений воркер. Тому поточний стан зберігається: служба
        // EcrWorker є → -EnableWorker. Відмова від воркера — рішення
        // адміністратора: `deploy-ecr.ps1 -DisableWorker`
        // (11-install-guide.md §2.6); майстер його не вимикає.
        // ⚠ -AllowExpress НЕ передається навмисно: на Express майстер має
        // зупинитись на кроці 1 з поясненням скрипта, як і сам скрипт.
        if (IsWorkerServiceInstalled())
        {
            ps.AddParameter("EnableWorker");
            OutputReceived?.Invoke("Служба EcrWorker уже встановлена — передаю -EnableWorker, щоб оновлення її зберегло.");
        }

        ps.Streams.Information.DataAdded += (_, e) => OutputReceived?.Invoke(FormatInformation(ps.Streams.Information[e.Index]));
        ps.Streams.Warning.DataAdded += (_, e) => OutputReceived?.Invoke("WARNING: " + ps.Streams.Warning[e.Index].Message);
        ps.Streams.Error.DataAdded += (_, e) => OutputReceived?.Invoke("ERROR: " + ps.Streams.Error[e.Index]);

        try
        {
            await Task.Run(() => ps.Invoke(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke("ERROR (script stopped): " + ex.Message);
            return false;
        }

        return !ps.HadErrors;
    }

    /// <summary>
    /// Чи зареєстровано службу EcrWorker — за ключем служби в реєстрі: той
    /// самий ключ, куди deploy-ecr.ps1 пише її Environment. Без
    /// System.ServiceProcess.ServiceController: це окремий пакет, а пакети
    /// тут не додаються.
    /// </summary>
    internal static bool IsWorkerServiceInstalled()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\EcrWorker");
        return key is not null;
    }

    private static string FormatInformation(InformationRecord record)
    {
        // Write-Host у скрипті потрапляє сюди як HostInformationMessage —
        // саме його .Message і є видимим текстом ("== Крок N/7: ... ==").
        return record.MessageData is HostInformationMessage host
            ? host.Message
            : record.MessageData?.ToString() ?? record.ToString();
    }
}
