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
public static class ProgramDataConfiguration
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
        var path = Path.Combine(
            commonApplicationDataFolder, "ECR", "config", "appsettings.Production.json");
        return builder.AddJsonFile(path, optional: true, reloadOnChange: true);
    }
}
