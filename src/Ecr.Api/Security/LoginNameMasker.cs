using System.Security.Cryptography;
using System.Text;
using Ecr.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Security;

/// <summary>Знеособлене ім'я входу для незмінного журналу: два перші символи, довжина, ключований хеш.</summary>
/// <param name="Prefix">Перші два символи (або менше, якщо ім'я коротше).</param>
/// <param name="Length">Довжина імені в символах.</param>
/// <param name="Hash">Перші 32 hex-символи HMAC-SHA256 від імені у верхньому регістрі; той самий ввід дає той самий хеш.</param>
public sealed record LoginNameMask(string Prefix, int Length, string Hash);

/// <summary>
/// Маскує ім'я входу, під яким НЕ знайдено облікового запису, перш ніж воно потрапить у незмінний <c>aud.SecurityEvent</c>.
/// </summary>
/// <remarks>
/// ⛔ Рішення координатора (приватність): люди іноді вводять ПАРОЛЬ у поле імені. У незмінному журналі (його не можна
/// ні виправити, ні прибрати 120 місяців) такого запису бути не може, тому для «невідомого імені» зберігається лише
/// маска: два перші символи, довжина й хеш. Повне ім'я лишається тільки в <c>sec.LoginAttempt</c>, яка чиститься за
/// строком (<c>Retention.TechnicalLogDays</c>).
/// </remarks>
public interface ILoginNameMasker
{
    /// <summary>Будує маску; не кидає винятків, окрім скасування (збій ключа — маска без хеша).</summary>
    /// <param name="userName">Ім'я, як його ввели.</param>
    /// <param name="ct">Скасування.</param>
    public Task<LoginNameMask> MaskAsync(string userName, CancellationToken ct);
}

/// <summary>
/// Ключ HMAC — випадкові 32 байти, захищені тим самим механізмом, що й решта секретів (Data Protection), у
/// <c>sys_ecr.SystemSetting</c> (ключ <see cref="SettingKey"/>): спільний для вузлів і переживає перезапуск, тож хеші
/// порівнювані, а без ключового кільця Data Protection (і без доступу до бази) підібрати за хешем пароль неможливо.
/// </summary>
public sealed class DataProtectedLoginNameMasker(IServiceScopeFactory scopes, IDataProtectionProvider protection)
    : ILoginNameMasker, IDisposable
{
    /// <summary>Ключ рядка в <c>sys_ecr.SystemSetting</c>.</summary>
    public const string SettingKey = "Security.LoginAuditHmacKey";

    private const string Purpose = "Ecr.LoginAudit.NameMask.v1";
    private const int HashHexLength = 32;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private byte[]? _key;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <inheritdoc />
    public async Task<LoginNameMask> MaskAsync(string userName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userName);

        var prefix = userName.Length <= 2 ? userName : userName[..2];
        string hash;
        try
        {
            var key = await GetKeyAsync(ct).ConfigureAwait(false);
            hash = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(userName.ToUpperInvariant())))
                .ToLowerInvariant()[..HashHexLength];
        }
#pragma warning disable CA1031 // Збій ключа не має ні зірвати вхід, ні виправдати запис сирого імені: маска без хеша.
        catch (Exception failure) when (failure is not OperationCanceledException)
#pragma warning restore CA1031
        {
            hash = string.Empty;
        }

        return new LoginNameMask(prefix, userName.Length, hash);
    }

    private async Task<byte[]> GetKeyAsync(CancellationToken ct)
    {
        if (_key is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_key is { } again)
            {
                return again;
            }

            var protector = protection.CreateProtector(Purpose);
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

            var stored = await ReadAsync(db, ct).ConfigureAwait(false);
            if (stored is not null && TryUnprotect(protector, stored, out var existing))
            {
                return _key = existing;
            }

            var created = RandomNumberGenerator.GetBytes(32);
            var protectedValue = protector.Protect(Convert.ToBase64String(created));
            var now = TimeProvider.System.GetUtcNow().UtcDateTime;

            // ⚠ Вставка, а за наявного (але нечитабельного — втрачене кільце Data Protection) рядка — заміна; гонка двох
            // вузлів закінчується повторним читанням: виграє той, хто записав першим.
            if (stored is null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    IF NOT EXISTS (SELECT 1 FROM sys_ecr.SystemSetting WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = {SettingKey})
                        INSERT INTO sys_ecr.SystemSetting ([Key], Value, Description, ModifiedAt)
                        VALUES ({SettingKey}, {protectedValue}, N'HMAC key for masked unknown sign-in names (PSS-24); protected by Data Protection', {now});
                    """, ct).ConfigureAwait(false);
            }
            else
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sys_ecr.SystemSetting SET Value = {protectedValue}, ModifiedAt = {now}
                    WHERE [Key] = {SettingKey} AND Value = {stored};
                    """, ct).ConfigureAwait(false);
            }

            var winner = await ReadAsync(db, ct).ConfigureAwait(false);
            return winner is not null && TryUnprotect(protector, winner, out var fromDb)
                ? _key = fromDb
                : _key = created;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Task<string?> ReadAsync(EcrDbContext db, CancellationToken ct)
        => db.Database
            .SqlQuery<string?>($"SELECT Value AS [Value] FROM sys_ecr.SystemSetting WHERE [Key] = {SettingKey}")
            .FirstOrDefaultAsync(ct);

    private static bool TryUnprotect(IDataProtector protector, string stored, out byte[] key)
    {
        try
        {
            key = Convert.FromBase64String(protector.Unprotect(stored));
            return key.Length == 32;
        }
#pragma warning disable CA1031 // Нечитабельний рядок (кільце втрачено, значення зіпсовано) — не помилка, а привід створити новий ключ.
        catch (Exception)
#pragma warning restore CA1031
        {
            key = [];
            return false;
        }
    }

    /// <summary>Лише для тестів: маска без бази й без Data Protection, з фіксованим ключем.</summary>
    /// <param name="userName">Ім'я.</param>
    /// <param name="key">Ключ HMAC.</param>
    internal static LoginNameMask MaskWith(string userName, byte[] key)
        => new(
            userName.Length <= 2 ? userName : userName[..2],
            userName.Length,
            Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(userName.ToUpperInvariant())))
                .ToLowerInvariant()[..HashHexLength]);
}
