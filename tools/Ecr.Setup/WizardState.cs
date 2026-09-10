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

/// <summary>
/// Спільний стан майстра — кожен крок читає з нього відповіді попередніх
/// кроків (передусім крок "Огляд") і записує власні відповіді через
/// <see cref="IWizardStep.Apply"/>. Паролі зберігаються як <see cref="SecureString"/>
/// від моменту введення до виклику <see cref="DeployRunner"/> — у відкритому
/// вигляді вони існують лише всередині побудови рядка підключення.
/// </summary>
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
}
