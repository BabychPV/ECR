using System.Runtime.InteropServices;
using System.Security;

namespace Ecr.Setup;

/// <summary>
/// Параметри виклику <c>deploy-ecr.ps1</c>, зібрані зі стану майстра.
/// </summary>
/// <remarks>
/// ⚠ Окремо від <c>DeployRunner</c>, і причина не стильова: той
/// хостить PowerShell (<c>Microsoft.PowerShell.SDK</c>), а перелік параметрів —
/// чиста функція стану, яку треба перевіряти тестом без PowerShell і без
/// Windows. <c>Value = null</c> — перемикач (<c>-FirstDeployment</c>).
/// </remarks>
internal static class DeployArguments
{
    /// <summary>Параметри в порядку передачі скрипту.</summary>
    /// <param name="state">Стан майстра після кроку «Огляд».</param>
    public static IReadOnlyList<KeyValuePair<string, object?>> Build(WizardState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var result = new List<KeyValuePair<string, object?>>
        {
            new("SqlInstance", state.SqlInstance),
            new("Database", state.Database),
            new("MsiPath", state.MsiPath),
            new("AppPort", state.Port),
            new("ConnectionString", BuildConnectionString(state)),

            // ⛔ Q-232: директива людини (2026-09-11) — майстер запускає
            // людина з доступом до бази, тож він завжди дозволяє
            // deploy-ecr.ps1 створити цільову базу самому, якщо її ще
            // немає, замість вимагати окремого кроку адміністратора БД
            // заздалегідь (`docs/build/11-install-guide.md` §0, оновлено).
            new("CreateDatabaseIfMissing", null),

            // ⛔ S11: без сертифіката служба в Production не стартує, і
            // deploy-ecr.ps1 зупиняється на кроці 1. Відбиток уже перевірено
            // майстром (крок сертифіката й «Огляд»), скрипт перевіряє ще раз.
            new("DataProtectionThumbprint", state.DataProtectionThumbprint),
        };

        if (state.Mode == WizardMode.FirstDeployment)
        {
            result.Add(new("FirstDeployment", null));
            if (state.BootstrapPassword is not null)
            {
                result.Add(new("BootstrapPassword", state.BootstrapPassword));
            }
        }

        if (state.SkipSchema)
        {
            result.Add(new("SkipSchema", null));
        }

        if (state.ServiceAccountMode != ServiceAccountMode.LocalSystem)
        {
            result.Add(new("ServiceAccount", state.ServiceAccountName));
        }

        if (state.ServiceAccountMode == ServiceAccountMode.DomainUser && state.ServicePassword is not null)
        {
            result.Add(new("ServicePassword", state.ServicePassword));
        }

        if (!state.SqlAuthIsWindows)
        {
            result.Add(new("SqlLogin", state.SqlLogin));
            result.Add(new("SqlPassword", state.SqlLoginPassword));
        }

        return result;
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
