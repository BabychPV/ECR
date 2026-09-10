using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.InteropServices;
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

        ps.AddCommand(scriptPath)
            .AddParameter("SqlInstance", state.SqlInstance)
            .AddParameter("Database", state.Database)
            .AddParameter("MsiPath", state.MsiPath)
            .AddParameter("AppPort", state.Port)
            .AddParameter("ConnectionString", BuildConnectionString(state));

        if (state.Mode == WizardMode.FirstDeployment)
        {
            ps.AddParameter("FirstDeployment");
            if (state.BootstrapPassword is not null)
            {
                ps.AddParameter("BootstrapPassword", state.BootstrapPassword);
            }
        }

        if (state.SkipSchema)
        {
            ps.AddParameter("SkipSchema");
        }

        if (state.ServiceAccountMode != ServiceAccountMode.LocalSystem)
        {
            ps.AddParameter("ServiceAccount", state.ServiceAccountName);
        }

        if (state.ServiceAccountMode == ServiceAccountMode.DomainUser && state.ServicePassword is not null)
        {
            ps.AddParameter("ServicePassword", state.ServicePassword);
        }

        if (!state.SqlAuthIsWindows)
        {
            ps.AddParameter("SqlLogin", state.SqlLogin);
            ps.AddParameter("SqlPassword", state.SqlLoginPassword);
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

    private static string FormatInformation(InformationRecord record)
    {
        // Write-Host у скрипті потрапляє сюди як HostInformationMessage —
        // саме його .Message і є видимим текстом ("== Крок N/7: ... ==").
        return record.MessageData is HostInformationMessage host
            ? host.Message
            : record.MessageData?.ToString() ?? record.ToString();
    }

    private static SecureString BuildConnectionString(WizardState state)
    {
        var text = state.SqlAuthIsWindows
            ? $"Server={state.SqlInstance};Database={state.Database};Trusted_Connection=True;TrustServerCertificate=True;"
            : $"Server={state.SqlInstance};Database={state.Database};User Id={state.SqlLogin};Password={ToPlain(state.SqlLoginPassword)};TrustServerCertificate=True;";
        return ToSecure(text);
    }

    private static string ToPlain(SecureString? secure)
    {
        if (secure is null)
        {
            return string.Empty;
        }

        var bstr = Marshal.SecureStringToBSTR(secure);
        try
        {
            return Marshal.PtrToStringBSTR(bstr);
        }
        finally
        {
            Marshal.ZeroFreeBSTR(bstr);
        }
    }

    private static SecureString ToSecure(string value)
    {
        var secure = new SecureString();
        foreach (var c in value)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }
}
