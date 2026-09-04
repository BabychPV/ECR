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

    /// <inheritdoc />
    public string Hash(string password)
        => throw new NotImplementedException(
            "TODO: RandomNumberGenerator.GetBytes(SaltSize); Rfc2898DeriveBytes.Pbkdf2 з " +
            "HashAlgorithmName.SHA512; зібрати рядок за форматом вище.");

    /// <inheritdoc />
    public bool Verify(string password, string hash)
        => throw new NotImplementedException(
            "TODO: розібрати формат; перерахувати; порівняти CryptographicOperations.FixedTimeEquals. " +
            "Звичайне порівняння масивів дає таймінг-атаку.");

    /// <inheritdoc />
    public bool NeedsRehash(string hash)
        => throw new NotImplementedException("TODO: розібрати параметри і порівняти з поточними.");
}
