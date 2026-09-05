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

    /// <summary>Вимикає джерело; збір із нього припиняється.</summary>
    public void Deactivate() => IsActive = false;
}
