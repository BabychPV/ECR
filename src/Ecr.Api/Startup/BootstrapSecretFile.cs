using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

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
    /// <param name="RejectedOwner">
    /// <c>null</c> — файл прийнято або його не було. Непорожнє — власник
    /// файлу не <c>BUILTIN\Administrators</c> і не <c>SYSTEM</c> (L10-02):
    /// пароль НЕ повертається, файл видаляється, викликач пише Critical.
    /// </param>
    public readonly record struct ReadResult(string? Password, string? DeleteError, string? RejectedOwner = null);

    /// <summary>
    /// Власник файлу, якщо він НЕ довірений, інакше <c>null</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ L10-02 (аудит 2026-10-03): файл кладе лише deploy-ecr.ps1 від імені
    /// адміністратора (власник — <c>BUILTIN\Administrators</c>, скрипт ставить
    /// його явно). Файл з будь-яким іншим власником підклав хтось інший — і
    /// пароль з нього став би паролем першого адміністратора системи. Поза
    /// Windows (dev, CI на Linux) власника Windows немає — перевірка
    /// пропускається.
    /// </remarks>
    public static string? UntrustedOwner(string path)
        => OperatingSystem.IsWindows() ? UntrustedOwnerWindows(path) : null;

    [SupportedOSPlatform("windows")]
    private static string? UntrustedOwnerWindows(string path)
    {
        var owner = new FileInfo(path).GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        if (owner is not null
            && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)))
        {
            return null;
        }

        return owner?.Value ?? "(невідомий)";
    }

    /// <summary>
    /// Читає пароль і одразу видаляє файл — незалежно від того, чи вдасться
    /// пароль потім використати.
    /// </summary>
    /// <param name="commonApplicationDataFolder">Тека спільних даних застосунків.</param>
    /// <param name="untrustedOwner">
    /// Перевірка власника; типово <see cref="UntrustedOwner"/>. Параметр — щоб
    /// відмову можна було перевірити тестом на будь-якій ОС.
    /// </param>
    public static ReadResult ReadAndDelete(
        string commonApplicationDataFolder, Func<string, string?>? untrustedOwner = null)
    {
        var path = PathUnder(commonApplicationDataFolder);
        if (!File.Exists(path))
        {
            return new ReadResult(null, null);
        }

        var rejectedOwner = (untrustedOwner ?? UntrustedOwner)(path);
        var content = rejectedOwner is null ? File.ReadAllText(path).Trim() : null;
        string? deleteError = null;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            deleteError = ex.Message;
        }

        return new ReadResult(string.IsNullOrEmpty(content) ? null : content, deleteError, rejectedOwner);
    }
}
