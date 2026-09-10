namespace Ecr.Api.Startup;

/// <summary>
/// Одноразовий файл bootstrap-пароля (директива №13, Q-215).
/// </summary>
/// <remarks>
/// ⛔ На відміну від рядка підключення (постійний секрет, живе в реєстрі
/// служби — Q-213), цей пароль потрібен РІВНО ОДНОМУ виклику
/// (<c>EnsureBootstrapAdminHandler.HandleAsync</c> у
/// <c>StartupSequence.cs</c>), що йде одразу після читання. Тримати його в
/// постійному сховищі далі — зайвий секрет, який інакше довелося б
/// прибирати вручну — саме це і закриває ця зміна.
/// </remarks>
public static class BootstrapSecretFile
{
    /// <summary>Ім'я файлу під <c>ECR\config</c>.</summary>
    public const string FileName = "bootstrap.secret";

    /// <summary>Повний шлях під заданою текою спільних даних застосунків.</summary>
    /// <param name="commonApplicationDataFolder">
    /// Типово — <c>Environment.SpecialFolder.CommonApplicationData</c>
    /// (<c>%ProgramData%</c>); параметр, а не читання середовища напряму,
    /// щоб шлях можна було підмінити в тестах (той самий прийом, що вже в
    /// <see cref="ProgramDataConfiguration"/>).
    /// </param>
    public static string PathUnder(string commonApplicationDataFolder)
        => Path.Combine(commonApplicationDataFolder, "ECR", "config", FileName);

    /// <summary>Результат спроби прочитати й прибрати файл.</summary>
    /// <param name="Password">
    /// <c>null</c> — файлу не було (звичайний стан: оновлення, чи повторний
    /// старт після успішного першого).
    /// </param>
    /// <param name="DeleteError">
    /// <c>null</c> — видалення вдалося або файлу й не було. Непорожнє —
    /// файл прочитано, але прибрати не вдалося: викликач має це залогувати,
    /// не проковтнути мовчки.
    /// </param>
    public readonly record struct ReadResult(string? Password, string? DeleteError);

    /// <summary>
    /// Читає пароль і одразу видаляє файл — незалежно від того, чи вдасться
    /// пароль потім використати.
    /// </summary>
    public static ReadResult ReadAndDelete(string commonApplicationDataFolder)
    {
        var path = PathUnder(commonApplicationDataFolder);
        if (!File.Exists(path))
        {
            return new ReadResult(null, null);
        }

        var content = File.ReadAllText(path).Trim();
        string? deleteError = null;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            deleteError = ex.Message;
        }

        return new ReadResult(string.IsNullOrEmpty(content) ? null : content, deleteError);
    }
}
