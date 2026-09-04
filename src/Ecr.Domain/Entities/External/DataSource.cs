// src/Ecr.Domain/Entities/External/DataSource.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Зовнішнє джерело даних. Транспорт — **поле, не гілка коду** (ФВ-11.2):
/// різні джерела можуть використовувати RTQP і Web API одночасно.
/// </summary>
/// <remarks>
/// <see cref="SecretName"/> — **лише ім'я** секрету, ніколи значення
/// (ФВ-6.11). Пароль сервісного облікового запису живе у сховищі секретів; у
/// нашій базі від нього є тільки посилання.
/// </remarks>
public sealed class DataSource : Entity<int>
{
    private DataSource() { }

    public DataSource(EcrCode code, LocalizedText name, ExternalTransport transport)
    {
        Code = code.Value;
        NameL10n = name;
        Transport = transport;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public ExternalTransport Transport { get; private set; }
    public string? Endpoint { get; private set; }

    /// <summary>Ім'я секрету. ⛔ Ніколи не значення.</summary>
    public string? SecretName { get; private set; }

    public bool IsActive { get; private set; }
}
