using System.Globalization;
using System.Security.Cryptography;
using Ecr.Application.Security;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Хешування паролів локальних облікових записів: PBKDF2-HMAC-SHA512.
/// </summary>
/// <remarks>
/// Формат: <c>{версія}.{ітерації}.{сіль-base64}.{хеш-base64}</c> — параметри
/// зберігаються поруч, щоб їх можна було посилити без міграції всіх паролів.
/// </remarks>
public sealed class PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 64;
    private const int Iterations = 210_000;

    /// <summary>Версія формату; змінюється разом з алгоритмом, не з параметрами.</summary>
    private const int Version = 1;

    /// <summary>
    /// Межа довжини пароля.
    /// </summary>
    /// <remarks>
    /// ⚠ Не косметичне обмеження: PBKDF2 читає пароль на кожній із 210 000
    /// ітерацій, тому рядок на кілька мегабайт перетворює одну спробу входу на
    /// відмову в обслуговуванні. Межа стоїть тут, а не лише в політиці, бо
    /// політику можна не застосувати, а цей метод обійти не можна.
    /// </remarks>
    private const int MaxPasswordLength = 256;

    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA512;

    /// <inheritdoc />
    public string Hash(string password)
    {
        // ⛔ Жодне повідомлення нижче не містить самого пароля — ні цілком, ні
        // фрагментом, ні довжиною понад ту, що вже названа межею (ФВ-6.11).
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (password.Length > MaxPasswordLength)
        {
            throw new ArgumentException(
                $"Пароль довший за {MaxPasswordLength} символів.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySize);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Version}.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}");
    }

    /// <inheritdoc />
    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
        {
            return false;
        }

        // Зіпсований рядок у базі — це «не збігається», а не 500: інакше один
        // пошкоджений запис перетворює вхід усіх на помилку сервера.
        if (!TryParse(hash, out var iterations, out var salt, out var expected))
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);

        // ⚠ Звичайне порівняння масивів завершується на першому розбіжному
        // байті й тим самим повідомляє, скільки байтів угадано. Різниця
        // мікроскопічна, але вимірювана — і саме на ній будують підбір.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <inheritdoc />
    public bool NeedsRehash(string hash)
        => !TryParse(hash, out var iterations, out var salt, out var key)
           || iterations != Iterations
           || salt.Length != SaltSize
           || key.Length != KeySize;

    /// <summary>Розбирає збережений рядок; <c>false</c> при будь-якій невідповідності.</summary>
    private static bool TryParse(
        string? hash, out int iterations, out byte[] salt, out byte[] key)
    {
        iterations = 0;
        salt = [];
        key = [];

        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        var parts = hash.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            || version != Version)
        {
            return false;
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations <= 0)
        {
            return false;
        }

        return TryDecode(parts[2], out salt) && TryDecode(parts[3], out key) && key.Length > 0;
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        var buffer = new byte[((value.Length + 3) / 4) * 3];
        if (Convert.TryFromBase64String(value, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }
}
