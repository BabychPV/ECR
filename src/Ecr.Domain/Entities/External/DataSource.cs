// src/Ecr.Domain/Entities/External/DataSource.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Зовнішнє джерело даних (<c>ext.DataSource</c>).
/// </summary>
/// <remarks>
/// ⛔ <see cref="SecretName"/> — **лише ім'я секрету, ніколи значення**
/// (ФВ-6.11, D-11). Пароль у цьому полі потрапив би в резервну копію, в
/// експорт конфігурації і в перший же скріншот конфігуратора.
/// <para>
/// Уся специфіка PI AF і Excel живе тільки в схемі <c>ext</c>: ядро про неї не
/// знає, і це перевіряється архітектурним тестом (ФВ-11.9).
/// </para>
/// </remarks>
public sealed class DataSource : Entity<int>
{
    private DataSource() { }

    /// <summary>Створює джерело.</summary>
    /// <param name="code">Код джерела.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="transport">Транспорт: RTQP за замовчуванням (ФВ-13.12).</param>
    /// <param name="endpoint">Адреса.</param>
    /// <param name="secretName">Ім'я секрету — не значення.</param>
    public DataSource(
        EcrCode code, LocalizedText name, ExternalTransport transport, string endpoint, string secretName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        Code = code.Value;
        NameL10n = name;
        Transport = transport;
        Endpoint = endpoint;
        SecretName = secretName;
        MaxParallel = DefaultMaxParallel;
        IsActive = true;
    }

    /// <summary>
    /// Скільки паралельних звернень до джерела за замовчуванням.
    /// </summary>
    /// <remarks>
    /// Обмеження на боці ECR, а не джерела: PI AF на надмірний паралелізм
    /// відповідає деградацією всім клієнтам, зокрема тим, що не наші.
    /// </remarks>
    private const int DefaultMaxParallel = 4;

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public ExternalTransport Transport { get; private set; }
    public string Endpoint { get; private set; } = null!;

    /// <summary>Запасна адреса для відмовостійкості; <c>null</c> — немає.</summary>
    public string? SecondaryEndpoint { get; private set; }

    /// <summary>⛔ Лише ІМ'Я секрету (ФВ-6.11). Значення живе поза базою.</summary>
    public string SecretName { get; private set; } = null!;

    /// <summary>Каталог або база джерела.</summary>
    public string? Catalog { get; private set; }

    public int MaxParallel { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Найбільша довжина логіна джерела (домен разом з іменем); та сама, що в стовпці.</summary>
    public const int UserNameMaxLength = 256;

    /// <summary>Як джерело входить у зовнішню систему; наявні рядки — <see cref="SourceAuthMode.Windows"/>.</summary>
    public SourceAuthMode AuthMode { get; private set; }

    /// <summary>Логін джерела (не секрет); лише за <see cref="SourceAuthMode.UserPassword"/>.</summary>
    public string? UserName { get; private set; }

    /// <summary>
    /// ⛔ Пароль джерела, УЖЕ зашифрований шаром, що володіє DataProtection (як SMTP, <c>D-263</c>). Назовні —
    /// лише <see cref="HasPassword"/>: ні значення, ні довжини.
    /// </summary>
    public byte[]? PasswordProtected { get; private set; }

    /// <summary>
    /// Коли джерело востаннє відмовило в автентифікації збереженим паролем; <c>null</c> — не відмовляло або пароль
    /// відтоді замінено. Поки задано, збір і перевірка за id до джерела не ходять (захист облікового запису від
    /// блокування, R-03).
    /// </summary>
    public DateTime? CredentialsRejectedAt { get; private set; }

    /// <summary>Чи заданий пароль — єдине, що про нього дозволено показувати.</summary>
    public bool HasPassword => PasswordProtected is { Length: > 0 };

    /// <summary>Версія рядка: дві правки одного з'єднання не затирають одна одну.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Налаштовує транспорт.</summary>
    /// <param name="secondaryEndpoint">Запасна адреса.</param>
    /// <param name="catalog">Каталог джерела.</param>
    /// <param name="maxParallel">Стеля паралельних звернень.</param>
    public void Configure(string? secondaryEndpoint, string? catalog, int maxParallel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallel);

        SecondaryEndpoint = secondaryEndpoint;
        Catalog = catalog;
        MaxParallel = maxParallel;
    }

    /// <summary>
    /// Змінює назву, транспорт, адресу і чи діє джерело (<c>BE-21</c>).
    /// </summary>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="transport">Транспорт.</param>
    /// <param name="endpoint">Адреса.</param>
    /// <param name="isActive">Чи збирати з джерела.</param>
    /// <remarks>
    /// ⛔ <see cref="Code"/> і <see cref="SecretName"/> сюди не входять. Код —
    /// природний ключ, на який посилаються сутності збору; ім'я секрету
    /// виводиться з коду і теж не є полем форми (ФВ-6.11, рішення людини на
    /// <c>Q15-06</c>: джерела ходять під службовим обліковим записом).
    ///
    /// ⚠ Транспорт змінний навмисно: вибір транспорту — налаштування, а не
    /// гілка коду (ФВ-11.2). Джерело, яке переїхало з RTQP на Web API, інакше
    /// довелося б заводити наново, втративши його сутності збору.
    /// </remarks>
    public void Update(LocalizedText name, ExternalTransport transport, string endpoint, bool isActive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        NameL10n = name;
        Transport = transport;
        Endpoint = endpoint;
        IsActive = isActive;
    }

    /// <summary>Чи транспорт уміє входити логіном і паролем (лише рядки з'єднання: PiSqlClient і Sql, R-11).</summary>
    /// <param name="transport">Транспорт.</param>
    public static bool SupportsUserPassword(ExternalTransport transport)
        => transport is ExternalTransport.PiSqlClient or ExternalTransport.Sql;

    /// <summary>
    /// Замінює облікові дані ЦІЛКОМ: режим, логін і вже зашифрований пароль (P-1126-SRCCRED). Мітку відмови скидає.
    /// </summary>
    /// <param name="authMode">Режим входу.</param>
    /// <param name="userName">Логін; за Windows ігнорується.</param>
    /// <param name="protectedPassword">Зашифрований пароль; за Windows ігнорується.</param>
    /// <remarks>
    /// ⛔ Інваріанти: Windows ⇒ ні логіна, ні пароля (перехід на Windows їх стирає); UserPassword ⇒ непорожні логін і
    /// пароль, і лише на транспорті, що вміє логін (<see cref="SupportsUserPassword"/>).
    /// </remarks>
    public void ReplaceCredentials(SourceAuthMode authMode, string? userName, byte[]? protectedPassword)
    {
        if (authMode == SourceAuthMode.UserPassword)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);

            if (protectedPassword is not { Length: > 0 } || !SupportsUserPassword(Transport))
            {
                throw new InvalidOperationException("UserPassword потребує пароля й транспорту PiSqlClient або Sql.");
            }
        }

        var windows = authMode == SourceAuthMode.Windows;
        AuthMode = authMode;
        UserName = windows ? null : userName;
        PasswordProtected = windows ? null : protectedPassword;
        CredentialsRejectedAt = null;
    }

    /// <summary>Джерело відмовило в автентифікації збереженим паролем: збір і перевірка за id далі не ходять (R-03).</summary>
    /// <param name="utcNow">Момент відмови, UTC.</param>
    public void MarkCredentialsRejected(DateTime utcNow)
    {
        if (AuthMode == SourceAuthMode.UserPassword)
        {
            CredentialsRejectedAt = utcNow;
        }
    }

    /// <summary>Вимикає джерело; збір із нього припиняється.</summary>
    public void Deactivate() => IsActive = false;
}
