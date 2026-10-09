using System.Globalization;

namespace Ecr.Setup;

/// <summary>Стан копії бази перед оновленням схеми (AN-117, S2-04).</summary>
internal enum BackupFreshness
{
    /// <summary>Повна чи диференційна копія в <c>msdb.dbo.backupset</c> не старша за межу.</summary>
    Fresh,

    /// <summary>Остання копія старша за межу.</summary>
    Stale,

    /// <summary>Копії в <c>msdb.dbo.backupset</c> немає зовсім.</summary>
    None,

    /// <summary>Перевірити не вдалося (немає прав на <c>msdb</c>, неочікувана відповідь).</summary>
    Unknown,
}

/// <summary>Результат перевірки копії: стан, вік (хвилини) і текст для людини.</summary>
/// <param name="Freshness">Стан.</param>
/// <param name="AgeMinutes">Вік останньої копії; <c>null</c> — копії немає чи вік невідомий.</param>
/// <param name="Detail">Пояснення англійською (мова інтерфейсу майстра).</param>
internal sealed record BackupCheckResult(BackupFreshness Freshness, int? AgeMinutes, string Detail);

/// <summary>
/// Правила кроку «копію бази зроблено» майстра в режимі оновлення (AN-117).
/// </summary>
/// <remarks>
/// ⛔ Дзеркало <c>Get-SchemaBackupProblem</c> із <c>deploy-ecr.ps1</c> (AN-110, S2-04): той самий запит до
/// <c>msdb.dbo.backupset</c> і та сама межа (<see cref="MaxAgeHours"/> = типовий <c>-BackupMaxAgeHours</c>).
/// Без цього кроку майстер в оновленні без свіжої копії в msdb доходив до «Install» і зупинявся на кроці 2/7
/// скрипта — а копію, зроблену поза SQL Server (VSS, вторинна репліка AG), не міг визнати ніяк.
/// <para>
/// ⛔ <c>-SkipBackupCheck</c> — ЛИШЕ після явної позначки людини (<see cref="WizardState.BackupRiskAccepted"/>);
/// свіжа копія в msdb його не вмикає: скрипт тоді перевірить її ще раз сам.
/// </para>
/// <para>⚠ Чиста логіка без WinForms і sqlcmd: файл підключено й до <c>Ecr.Architecture.Tests</c>.</para>
/// </remarks>
internal static class SchemaBackupRules
{
    /// <summary>Найстаріша прийнятна копія, години (= типовий <c>-BackupMaxAgeHours</c> скрипта).</summary>
    public const int MaxAgeHours = 24;

    /// <summary>Чи потрібна перевірка копії: лише оновлення, у якому скрипт змінює схему.</summary>
    public static bool IsRequired(WizardState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Mode == WizardMode.Update && !state.SkipSchema;
    }

    /// <summary>
    /// Запит віку останньої повної/диференційної копії, хвилини або <c>none</c> — дослівно той, що виконує
    /// <c>deploy-ecr.ps1</c> на кроці 2/7.
    /// </summary>
    /// <param name="database">Назва бази.</param>
    public static string Query(string database)
    {
        ArgumentNullException.ThrowIfNull(database);

        return "SET NOCOUNT ON; SELECT ISNULL(CAST(DATEDIFF(MINUTE, MAX(backup_finish_date), GETDATE()) AS nvarchar(20)), N'none') "
            + $"FROM msdb.dbo.backupset WHERE database_name = N'{database.Replace("'", "''", StringComparison.Ordinal)}' AND type IN ('D', 'I');";
    }

    /// <summary>Оцінка відповіді запиту <see cref="Query"/> (останній непорожній рядок виводу sqlcmd).</summary>
    /// <param name="output">Вивід sqlcmd.</param>
    /// <param name="database">Назва бази — для тексту.</param>
    /// <param name="maxAgeHours">Межа, години.</param>
    public static BackupCheckResult Assess(string? output, string database, int maxAgeHours = MaxAgeHours)
    {
        var raw = (output ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? string.Empty;

        if (raw.Length == 0 || string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                BackupFreshness.None, null,
                $"There is no full or differential backup of database {database} in msdb.dbo.backupset.");
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return Unknown($"Unexpected answer to the backup query for database {database}: '{raw}'.");
        }

        var hours = (minutes / 60.0).ToString("0.0", CultureInfo.InvariantCulture);
        return minutes > maxAgeHours * 60
            ? new(
                BackupFreshness.Stale, minutes,
                $"The last backup of database {database} was made {hours} h ago - older than {maxAgeHours} h.")
            : new(
                BackupFreshness.Fresh, minutes,
                $"The last backup of database {database} was made {hours} h ago (msdb.dbo.backupset).");
    }

    /// <summary>Перевірити не вдалося.</summary>
    /// <param name="detail">Чому.</param>
    public static BackupCheckResult Unknown(string detail) => new(BackupFreshness.Unknown, null, detail);

    /// <summary>Рядок «Database backup» на кроці Review; <paramref name="warning"/> — червоний рядок.</summary>
    /// <param name="state">Стан майстра.</param>
    /// <param name="warning">Перевірку пропущено (<c>-SkipBackupCheck</c>).</param>
    public static string Describe(WizardState state, out bool warning)
    {
        ArgumentNullException.ThrowIfNull(state);

        warning = state.SkipBackupCheck;
        if (state.Mode == WizardMode.FirstDeployment)
        {
            return "not required (first deployment)";
        }

        if (state.SkipSchema)
        {
            return "not checked (the schema is not applied)";
        }

        if (state.SkipBackupCheck)
        {
            return "WARNING: check SKIPPED (-SkipBackupCheck) - you confirmed a backup made outside SQL Server "
                + "or accepted the risk; rollback (runbook 9) is impossible without it";
        }

        return state.Backup is { Freshness: BackupFreshness.Fresh } fresh
            ? $"checked: {fresh.Detail} deploy-ecr.ps1 checks it again"
            : $"deploy-ecr.ps1 requires a backup not older than {MaxAgeHours} h (msdb.dbo.backupset)";
    }
}
