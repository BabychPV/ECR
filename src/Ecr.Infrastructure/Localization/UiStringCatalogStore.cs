using System.Data;
using System.Runtime.CompilerServices;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Caching;
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

    /// <summary>Ключ кешу переліку увімкнених мов (L1-02; PSS-29 — і для публічного початкового стану).</summary>
    internal const string EnabledLanguagesKey = "ui:languages:enabled";

    /// <summary>Ключ мемо поточної ревізії каталогу (PSS-29).</summary>
    internal const string RevisionKey = "ui:revision";

    /// <summary>
    /// Скільки живе перелік увімкнених мов у кеші.
    /// </summary>
    /// <remarks>
    /// Хвилина: мови вмикаються рідко (лише сідом — застосунок реєстр мов не пише), а без кешу кожен анонімний
    /// запит із вигаданою мовою знову бив би по базі — рівно те, від чого захищає L1-02.
    /// </remarks>
    private static readonly TimeSpan EnabledLanguagesLifetime = TimeSpan.FromMinutes(1);

    /// <summary>Вікно мемо ревізії — ті самі 5 с, що в <c>MetadataCache</c> і <c>SecurityStampValidator</c> (`RD-05`).</summary>
    internal static readonly TimeSpan RevisionWindow = CacheLifetimes.DefaultRevision;

    /// <summary>Польоти за сховищем кешу: тестові хости мають кожен своє сховище і не мають ділити побудов.</summary>
    private static readonly ConditionalWeakTable<IMemoryCache, SingleFlight<object>> Flights = new();

    /// <summary>Порівняння-і-запис мемо ревізії (<see cref="RememberRevision"/>).</summary>
    private static readonly Lock RevisionGate = new();

    /// <inheritdoc />
    public Task<UiStringCatalog> GetAsync(string languageCode, CancellationToken ct)
        => LoadAsync(languageCode, scope: null, ct);

    /// <inheritdoc />
    public Task<UiStringCatalog> GetScopedAsync(string languageCode, UiStringScope scope, CancellationToken ct)
        => LoadAsync(languageCode, scope, ct);

    /// <inheritdoc />
    /// <remarks>⚠ Свіже читання (імпорт перекладу звіряє ревізію) — і заодно освіжає мемо гарячого шляху.</remarks>
    public async Task<int> GetRevisionAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var revision = await ReadRevisionAsync(connection, ct).ConfigureAwait(false);
        RememberRevision(revision);
        return revision;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ PSS-29: з кешу на <see cref="EnabledLanguagesLifetime"/> і одним польотом. Це читання анонімного
    /// <c>GET /api/v1/public/bootstrap</c> (кожне відкриття екрана входу) — без кешу воно йшло в базу щоразу.
    /// </remarks>
    public async Task<IReadOnlyList<LanguageDto>> ListLanguagesAsync(CancellationToken ct)
    {
        if (memory.TryGetValue(EnabledLanguagesKey, out IReadOnlyList<LanguageDto>? cached) && cached is not null)
        {
            return cached;
        }

        var connectionString = ConnectionString;
        var built = await Flight
            .RunAsync(
                EnabledLanguagesKey,
                async token =>
                {
                    if (memory.TryGetValue(EnabledLanguagesKey, out IReadOnlyList<LanguageDto>? ready)
                        && ready is not null)
                    {
                        return ready;
                    }

                    var languages = await ReadLanguagesAsync(connectionString, token).ConfigureAwait(false);
                    memory.Set(EnabledLanguagesKey, languages, EnabledLanguagesLifetime);
                    return languages;
                },
                ct)
            .ConfigureAwait(false);

        return (IReadOnlyList<LanguageDto>)built;
    }

    /// <summary>Читає увімкнені мови з реєстру.</summary>
    private static async Task<IReadOnlyList<LanguageDto>> ReadLanguagesAsync(
        string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ Лише увімкнені: вимкнена мова лишається в таблиці разом зі своїми
        // перекладами, але пропонувати її в полі назви означало б заводити
        // тексти, яких ніхто не побачить.
        command.CommandText =
            "SELECT Code, NameNative, IsDefault FROM sys_ecr.Language "
            + "WHERE IsActive = 1 ORDER BY Ordinal;";

        var result = new List<LanguageDto>();

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new LanguageDto(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        // Спільний для всіх запитів екземпляр — лише для читання.
        return result.AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Те саме правило, що клієнт застосовував до каталогів (<c>useTranslatedLanguages</c>): у мові є рядок, якого
    /// немає мовою за замовчуванням, або текст якого відрізняється від неї. ⚠ Порівняння побайтове
    /// (<c>Latin1_General_BIN2</c>): правка лише регістру — теж переклад, як і при порівнянні рядків у клієнті.
    /// </remarks>
    public async Task<IReadOnlySet<string>?> ListTranslatedLanguagesAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT l.Code FROM sys_ecr.Language l "
            + "WHERE l.IsActive = 1 AND l.IsDefault = 0 AND EXISTS ("
            + "SELECT 1 FROM sys_ecr.UiString s "
            + "LEFT JOIN sys_ecr.UiString d ON d.[Key] = s.[Key] "
            + "AND d.LanguageCode = (SELECT TOP (1) Code FROM sys_ecr.Language WHERE IsDefault = 1) "
            + "WHERE s.LanguageCode = l.Code "
            + "AND (d.[Key] IS NULL OR s.Value COLLATE Latin1_General_BIN2 <> d.Value COLLATE Latin1_General_BIN2));";

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<string> ResolveLanguageAsync(string languageCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return UiStringResolver.DefaultLanguage;
        }

        // ⚠ Той самий кешований перелік, що й у публічному початковому стані: один запис, одне вікно свіжості.
        var enabled = await ListLanguagesAsync(ct).ConfigureAwait(false);

        // Повертається код із РЕЄСТРУ, а не з запиту: «RU» і «ru» — один запис кешу.
        return enabled
                   .Select(l => l.Code)
                   .FirstOrDefault(code => string.Equals(code, languageCode, StringComparison.OrdinalIgnoreCase))
               ?? UiStringResolver.DefaultLanguage;
    }

    /// <inheritdoc />
    public async Task<string?> FindKeyAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Те саме порівняння (колація стовпця), що й в UPDATE у SetAsync.
        command.CommandText = "SELECT TOP (1) [Key] FROM sys_ecr.UiString WHERE [Key] = @key;";
        command.Parameters.AddWithValue("@key", key);

        var raw = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return raw as string;
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

        // ⚠ Мемо гарячого шляху — одразу: термінолог бачить свою правку без вікна свіжості.
        RememberRevision(revision);

        return new UiStringWriteResult(revision, previous);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Без кешу: це екран термінолога, а не шлях кожного запиту, і він
    /// зобов'язаний показати щойно збережений переклад.
    /// </remarks>
    public async Task<IReadOnlyList<UiStringRawRow>> ListRawAsync(string languageCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var defaults = await ReadStringsAsync(connection, UiStringResolver.DefaultLanguage, scope: null, ct)
            .ConfigureAwait(false);
        var requested =
            string.Equals(languageCode, UiStringResolver.DefaultLanguage, StringComparison.OrdinalIgnoreCase)
                ? defaults
                : await ReadStringsAsync(connection, languageCode, scope: null, ct).ConfigureAwait(false);

        return UiStringResolver.ComposeRaw(defaults, requested);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UiStringExportRow>> ListForExportAsync(string languageCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Порожній переклад = відсутній, як у ComposeRaw.
        command.CommandText = """
            SELECT e.[Key], e.Scope, e.Value, NULLIF(t.Value, N''),
                   CASE WHEN t.Value <> N'' THEN t.ModifiedAt END
              FROM sys_ecr.UiString e
              LEFT JOIN sys_ecr.UiString t ON t.[Key] = e.[Key] AND t.LanguageCode = @lang
             WHERE e.LanguageCode = @default
             ORDER BY e.[Key];
            """;
        command.Parameters.AddWithValue("@lang", languageCode);
        command.Parameters.AddWithValue("@default", UiStringResolver.DefaultLanguage);

        var rows = new List<UiStringExportRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new UiStringExportRow(
                reader.GetString(0),
                (UiStringScope)reader.GetByte(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
        }

        // Порядок — ординальний, як у ComposeRaw, а не за колацією бази.
        return [.. rows.OrderBy(r => r.Key, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<int> SetManyAsync(IReadOnlyList<UiStringWrite> writes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(writes);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        if (writes.Count == 0)
        {
            var current = await ReadRevisionAsync(connection, ct).ConfigureAwait(false);
            RememberRevision(current);
            return current;
        }

        await using var transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        foreach (var write in writes)
        {
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                UPDATE sys_ecr.UiString
                   SET Value = @value, Scope = @scope, ModifiedAt = @at, ModifiedByUserId = @user
                 WHERE [Key] = @key AND LanguageCode = @lang;
                IF @@ROWCOUNT = 0
                    INSERT sys_ecr.UiString ([Key], LanguageCode, Value, Scope, ModifiedAt, ModifiedByUserId)
                    VALUES (@key, @lang, @value, @scope, @at, @user);
                """;
            upsert.Parameters.AddWithValue("@key", write.Key);
            upsert.Parameters.AddWithValue("@lang", write.LanguageCode);
            upsert.Parameters.AddWithValue("@value", write.Value);
            upsert.Parameters.AddWithValue("@scope", (byte)write.Scope);
            upsert.Parameters.AddWithValue("@at", write.ModifiedAt);
            upsert.Parameters.AddWithValue("@user", (object?)write.ModifiedByUserId ?? DBNull.Value);
            await upsert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int revision;
        await using (var bump = connection.CreateCommand())
        {
            bump.Transaction = transaction;
            bump.CommandText = """
                UPDATE sys_ecr.UiStringRevision
                   SET Revision = Revision + 1, ModifiedAt = @at
                OUTPUT inserted.Revision
                 WHERE Id = 1;
                """;
            bump.Parameters.AddWithValue("@at", writes[0].ModifiedAt);
            var raw = await bump.ExecuteScalarAsync(ct).ConfigureAwait(false);
            revision = raw is null or DBNull ? 0 : Convert.ToInt32(raw, provider: null);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        RememberRevision(revision);
        return revision;
    }

    /// <inheritdoc />
    public async Task<bool> LanguageExistsAsync(string languageCode, CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys_ecr.Language WHERE Code = @lang;";
        command.Parameters.AddWithValue("@lang", languageCode);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), provider: null) > 0;
    }

    /// <summary>Читає зріз мови з кешу або з бази.</summary>
    /// <remarks>
    /// ⛔ PSS-29 (e2e RC #988): гарячий шлях — БЕЗ бази. Тут відкривалося з'єднання й читалася ревізія на КОЖЕН
    /// запит, навіть коли зріз лежав у кеші; анонімний екран входу тягне 3–5 таких запитів на завантаження, тож
    /// базу «тримала» лише межа частоти на адресу — а вона ж клала екран входу всім за NAT. Тепер ревізія —
    /// мемо на <see cref="RevisionWindow"/>, мови — на хвилину, зріз — до зміни ревізії, і кожне читання з бази
    /// йде одним польотом (<see cref="SingleFlight{T}"/>) на ключ: скільки б запитів не прийшло з будь-скількох
    /// адрес, база бачить ≤ 1 читання ревізії на вікно й ≤ 1 побудову зрізу на (мову, область, ревізію).
    /// </remarks>
    private async Task<UiStringCatalog> LoadAsync(string languageCode, UiStringScope? scope, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);

        // ⛔ L1-02: ключ кешу — лише код увімкненої мови. Невідома мова
        // обслуговується записом мови за замовчуванням, а не новим ключем.
        languageCode = await ResolveLanguageAsync(languageCode, ct).ConfigureAwait(false);

        var revision = await CurrentRevisionAsync(ct).ConfigureAwait(false);

        // Ключ {lang}:{scope}:{revision} — нова версія дає новий ключ, тому
        // інвалідація не потрібна і два інстанси не розійдуться.
        var key = $"ui:{languageCode}:{scope?.ToString() ?? "all"}:{revision}";
        if (memory.TryGetValue(key, out UiStringCatalog? cached) && cached is not null)
        {
            return cached;
        }

        var connectionString = ConnectionString;
        var built = await Flight
            .RunAsync(
                key,
                async token =>
                {
                    if (memory.TryGetValue(key, out UiStringCatalog? ready) && ready is not null)
                    {
                        return ready;
                    }

                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(token).ConfigureAwait(false);

                    var requested = await ReadStringsAsync(connection, languageCode, scope, token)
                        .ConfigureAwait(false);

                    // Fallback читається лише коли мова НЕ є мовою за замовчуванням —
                    // інакше кожен запит англійською коштував би два запити.
                    var defaults =
                        string.Equals(languageCode, UiStringResolver.DefaultLanguage, StringComparison.OrdinalIgnoreCase)
                            ? requested
                            : await ReadStringsAsync(connection, UiStringResolver.DefaultLanguage, scope, token)
                                .ConfigureAwait(false);

                    var catalog = new UiStringCatalog(
                        languageCode, revision, UiStringResolver.Compose(defaults, requested));

                    memory.Set(key, catalog, Lifetime);
                    return catalog;
                },
                ct)
            .ConfigureAwait(false);

        return (UiStringCatalog)built;
    }

    /// <summary>Поточна ревізія каталогу — з мемо на <see cref="RevisionWindow"/>.</summary>
    /// <remarks>
    /// ⚠ Ціна названа прямо (як у <c>MetadataCache</c>, `RD-05`): правка перекладу на ІНШОМУ вузлі доходить сюди
    /// за ≤ 5 с. На цьому вузлі — одразу: запис кладе в мемо нову ревізію сам (<see cref="RememberRevision"/>).
    /// </remarks>
    private async Task<int> CurrentRevisionAsync(CancellationToken ct)
    {
        if (memory.TryGetValue(RevisionKey, out int memoized))
        {
            return memoized;
        }

        var connectionString = ConnectionString;
        var built = await Flight
            .RunAsync(
                RevisionKey,
                async token =>
                {
                    if (memory.TryGetValue(RevisionKey, out int ready))
                    {
                        return ready;
                    }

                    await using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync(token).ConfigureAwait(false);
                    return RememberRevision(await ReadRevisionAsync(connection, token).ConfigureAwait(false));
                },
                ct)
            .ConfigureAwait(false);

        return (int)built;
    }

    /// <summary>Кладе ревізію в мемо, якщо вона не старша за ту, що вже там.</summary>
    /// <remarks>
    /// ⛔ Ревізія лише зростає, тому злиття — «більша перемагає». Без цього читання, що стартувало ДО запису й
    /// закінчилося ПІСЛЯ нього, повернуло б у мемо стару ревізію на ціле вікно — і щойно збережений переклад не
    /// був би видний навіть на вузлі, де його зберегли.
    /// </remarks>
    /// <param name="revision">Прочитана або щойно записана ревізія.</param>
    /// <returns>Ревізія, що лежить у мемо після злиття.</returns>
    private int RememberRevision(int revision)
    {
        lock (RevisionGate)
        {
            if (memory.TryGetValue(RevisionKey, out int known) && known >= revision)
            {
                return known;
            }

            memory.Set(RevisionKey, revision, RevisionWindow);
            return revision;
        }
    }

    /// <summary>Рядок з'єднання — рядком, а не <c>DbContext</c>: спільна побудова може пережити scope власника.</summary>
    private string ConnectionString => db.Database.GetConnectionString()
        ?? throw new InvalidOperationException("Рядок з'єднання з базою не задано.");

    /// <summary>Один політ на ключ — свій на кожне сховище кешу (тестові хости не ділять побудов між собою).</summary>
    private SingleFlight<object> Flight => Flights.GetValue(memory, static _ => new SingleFlight<object>());

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

        // ⛔ `Private` — це НЕ «рядки з позначкою Private», а «все, що видно
        // після входу», тобто разом із публічними. Точна рівність `Scope = 1`
        // (як було до `A7-12`) означала, що після перезавантаження сторінки вже
        // авторизованим користувачем зникали `common.save` і тексти помилок:
        // публічний каталог у цьому сценарії ніхто не запитує, а продублювати
        // ключ у двох областях не дає `PK_UiString ([Key], LanguageCode)`.
        command.CommandText = scope is UiStringScope.Public
            ? "SELECT [Key], Value FROM sys_ecr.UiString WHERE LanguageCode = @lang AND Scope = 0;"
            : "SELECT [Key], Value FROM sys_ecr.UiString WHERE LanguageCode = @lang;";

        command.Parameters.AddWithValue("@lang", languageCode);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }
}
