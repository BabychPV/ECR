using System.Data;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Localization;

/// <summary>
/// Каталог рядків інтерфейсу над <c>sys_ecr.UiString</c> (ФВ-14.9, D-95).
/// </summary>
/// <remarks>
/// ⚠ Таблиця живе поза моделлю EF: це не предметна модель, а довідник рядків,
/// який читається зрізом на мову і кешується за <c>ETag</c>. Тому тут прямий
/// ADO, а створює таблицю скрипт <c>08-system-tables.sql</c>, а не міграція.
/// </remarks>
public sealed class UiStringCatalogStore(EcrDbContext db, IMemoryCache memory) : IUiStringCatalog
{
    /// <summary>
    /// Стеля життя запису кешу.
    /// </summary>
    /// <remarks>
    /// Інвалідація не потрібна: версія входить у ключ, тому запис змінює ключ,
    /// а не псує наявний (та сама схема, що з метаданими, D-16). Стеля потрібна
    /// лише щоб зрізи старих версій не жили в пам'яті до перезапуску.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public Task<UiStringCatalog> GetAsync(string languageCode, CancellationToken ct)
        => LoadAsync(languageCode, scope: null, ct);

    /// <inheritdoc />
    public Task<UiStringCatalog> GetScopedAsync(string languageCode, UiStringScope scope, CancellationToken ct)
        => LoadAsync(languageCode, scope, ct);

    /// <inheritdoc />
    public async Task<int> GetRevisionAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return await ReadRevisionAsync(connection, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UiStringWriteResult> SetAsync(UiStringWrite write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        UiStringScope? previous;
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;

            // OUTPUT deleted.Scope дає ПОПЕРЕДНЮ область тим самим statement:
            // окреме читання перед записом показало б стан, який до моменту
            // запису вже міг змінитися.
            upsert.CommandText = """
                DECLARE @prev TABLE (Scope tinyint NOT NULL);

                UPDATE sys_ecr.UiString
                   SET Value = @value, Scope = @scope,
                       ModifiedAt = @at, ModifiedByUserId = @user
                OUTPUT deleted.Scope INTO @prev
                 WHERE [Key] = @key AND LanguageCode = @lang;

                IF NOT EXISTS (SELECT 1 FROM @prev)
                    INSERT sys_ecr.UiString ([Key], LanguageCode, Value, Scope, ModifiedAt, ModifiedByUserId)
                    VALUES (@key, @lang, @value, @scope, @at, @user);

                SELECT TOP (1) Scope FROM @prev;
                """;

            upsert.Parameters.AddWithValue("@key", write.Key);
            upsert.Parameters.AddWithValue("@lang", write.LanguageCode);
            upsert.Parameters.AddWithValue("@value", write.Value);
            upsert.Parameters.AddWithValue("@scope", (byte)write.Scope);
            upsert.Parameters.AddWithValue("@at", write.ModifiedAt);
            upsert.Parameters.AddWithValue("@user", (object?)write.ModifiedByUserId ?? DBNull.Value);

            var raw = await upsert.ExecuteScalarAsync(ct).ConfigureAwait(false);
            previous = raw is null or DBNull ? null : (UiStringScope)Convert.ToByte(raw, provider: null);
        }

        int revision;
        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;

            // ⚠ Інкремент і читання нової версії — ОДИН statement із OUTPUT
            // (R-B7). Два адміністратори, що правлять переклад одночасно, інакше
            // отримали б однакову версію, і другий ETag збігся б із першим при
            // різному вмісті.
            bump.CommandText = """
                UPDATE sys_ecr.UiStringRevision
                   SET Revision = Revision + 1, ModifiedAt = @at
                OUTPUT inserted.Revision
                 WHERE Id = 1;
                """;
            bump.Parameters.AddWithValue("@at", write.ModifiedAt);

            var raw = await bump.ExecuteScalarAsync(ct).ConfigureAwait(false);
            revision = raw is null or DBNull ? 0 : Convert.ToInt32(raw, provider: null);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new UiStringWriteResult(revision, previous);
    }

    /// <summary>Читає зріз мови з кешу або з бази.</summary>
    private async Task<UiStringCatalog> LoadAsync(string languageCode, UiStringScope? scope, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var revision = await ReadRevisionAsync(connection, ct).ConfigureAwait(false);

        // Ключ {lang}:{scope}:{revision} — нова версія дає новий ключ, тому
        // інвалідація не потрібна і два інстанси не розійдуться.
        var key = $"ui:{languageCode}:{scope?.ToString() ?? "all"}:{revision}";
        if (memory.TryGetValue(key, out UiStringCatalog? cached) && cached is not null)
        {
            return cached;
        }

        var requested = await ReadStringsAsync(connection, languageCode, scope, ct).ConfigureAwait(false);

        // Fallback читається лише коли мова НЕ є мовою за замовчуванням —
        // інакше кожен запит англійською коштував би два запити.
        var defaults =
            string.Equals(languageCode, UiStringResolver.DefaultLanguage, StringComparison.OrdinalIgnoreCase)
                ? requested
                : await ReadStringsAsync(connection, UiStringResolver.DefaultLanguage, scope, ct)
                    .ConfigureAwait(false);

        var catalog = new UiStringCatalog(
            languageCode, revision, UiStringResolver.Compose(defaults, requested));

        memory.Set(key, catalog, Lifetime);
        return catalog;
    }

    private static async Task<int> ReadRevisionAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Revision FROM sys_ecr.UiStringRevision WHERE Id = 1;";

        var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return raw is null or DBNull ? 0 : Convert.ToInt32(raw, provider: null);
    }

    private static async Task<Dictionary<string, string>> ReadStringsAsync(
        SqlConnection connection, string languageCode, UiStringScope? scope, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = scope is null
            ? "SELECT [Key], Value FROM sys_ecr.UiString WHERE LanguageCode = @lang;"
            : "SELECT [Key], Value FROM sys_ecr.UiString WHERE LanguageCode = @lang AND Scope = @scope;";

        command.Parameters.AddWithValue("@lang", languageCode);
        if (scope is not null)
        {
            command.Parameters.AddWithValue("@scope", (byte)scope.Value);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }
}
