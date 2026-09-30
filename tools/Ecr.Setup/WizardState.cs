using System.Security;

namespace Ecr.Setup;

/// <summary>
/// Спосіб розгортання, обраний на кроці 1 — визначає, чи показувати крок
/// "Пароль адміністратора" і чи пропонувати "-SkipSchema" на кроці бази даних.
/// </summary>
internal enum WizardMode
{
    FirstDeployment,
    Update,
}

/// <summary>Обліковий запис, під яким запускається служба (крок 2).</summary>
internal enum ServiceAccountMode
{
    LocalSystem,
    Gmsa,
    DomainUser,
}

/// <summary>Транспорт служби (крок «Транспорт», D14-08) — відповідає параметру deploy-ecr.ps1.</summary>
internal enum WizardTransport
{
    /// <summary><c>-HttpsThumbprint</c>: Kestrel слухає HTTPS із сертифікатом замовника.</summary>
    Https,

    /// <summary><c>-BehindHttpsProxy</c>: TLS завершується на проксі перед застосунком.</summary>
    Proxy,

    /// <summary><c>-AllowHttp</c>: лише стенд, cookie сеансу не Secure.</summary>
    Http,
}

/// <summary>
/// Спільний стан майстра — кожен крок читає з нього відповіді попередніх
/// кроків (передусім крок "Огляд") і записує власні відповіді через
/// <c>IWizardStep.Apply</c>. Паролі зберігаються як <see cref="SecureString"/>
/// від моменту введення до виклику <c>DeployRunner</c> — у відкритому
/// вигляді вони існують лише всередині побудови рядка підключення.
/// </summary>
/// <remarks>
/// ⚠ <c>&lt;c&gt;</c>, а не <c>cref</c>, на типи WinForms/PowerShell-частини
/// майстра: цей файл також компілюється в <c>Ecr.Architecture.Tests</c>
/// (S11), де тих типів немає.
/// </remarks>
internal sealed class WizardState
{
    // Крок 1 — режим.
    public WizardMode Mode { get; set; } = WizardMode.FirstDeployment;

    // Крок 2 — обліковий запис і мережа.
    public ServiceAccountMode ServiceAccountMode { get; set; } = ServiceAccountMode.LocalSystem;
    public string? ServiceAccountName { get; set; }
    public SecureString? ServicePassword { get; set; }
    public int Port { get; set; } = 5000;
    public string? MsiPath { get; set; }

    // Крок 3 — база даних.
    public string SqlInstance { get; set; } = @"localhost\SQLEXPRESS";
    public string Database { get; set; } = "ECR";
    public bool SqlAuthIsWindows { get; set; } = true;
    public string? SqlLogin { get; set; }
    public SecureString? SqlLoginPassword { get; set; }
    public bool SkipSchema { get; set; }

    // Крок 4 — пароль адміністратора (лише для FirstDeployment).
    public SecureString? BootstrapPassword { get; set; }

    // Крок «Сертифікат Data Protection» (S11): відбиток із Cert:\LocalMachine\My.
    // Не секрет — хеш публічного сертифіката; тому звичайний рядок.
    private string? _dataProtectionThumbprint;

    public string? DataProtectionThumbprint
    {
        get => _dataProtectionThumbprint;
        set
        {
            var normalized = DataProtectionCertificateRules.Normalize(value);
            _dataProtectionThumbprint = normalized.Length == 0 ? null : normalized;
        }
    }

    /// <summary>
    /// Чи годиться збережений відбиток зараз (S11) — перед стартом
    /// розгортання, а не лише при виборі на кроці.
    /// </summary>
    public bool TryValidateDataProtection(ICertificateSource source, DateTime now, out string error)
        => DataProtectionCertificateRules.TryValidate(DataProtectionThumbprint, source, now, out error);

    // Крок «Транспорт» (D14-08): HTTPS із сертифікатом (типово — рекомендований), TLS на проксі
    // або HTTP лише для стенда. Явний вибір — deploy-ecr.ps1 без жодного з трьох зупиняється.
    public WizardTransport Transport { get; set; } = WizardTransport.Https;

    private string? _httpsThumbprint;

    /// <summary>Відбиток сертифіката HTTPS; не секрет — хеш публічного сертифіката.</summary>
    public string? HttpsThumbprint
    {
        get => _httpsThumbprint;
        set
        {
            var normalized = DataProtectionCertificateRules.Normalize(value);
            _httpsThumbprint = normalized.Length == 0 ? null : normalized;
        }
    }

    /// <summary>
    /// Чи годиться вибраний транспорт зараз: для HTTPS — сертифікат у сховищі, із ключем, чинний;
    /// для проксі й HTTP перевіряти нічого.
    /// </summary>
    public bool TryValidateTransport(ICertificateSource source, DateTime now, out string error)
    {
        if (Transport != WizardTransport.Https)
        {
            error = string.Empty;
            return true;
        }

        return HttpsCertificateRules.TryValidate(HttpsThumbprint, source, now, out error);
    }
}
