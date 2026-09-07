using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Визначення реєстру. **Один механізм на всі довідники** — від плоского
/// списку до <c>Permit</c> із каскадами і M:N (ФВ-8.1).
/// </summary>
public sealed class RegistryDef : Entity<int>
{
    private readonly List<RegistryFieldDef> _fields = [];

    private RegistryDef() { }

    public RegistryDef(EcrCode code, LocalizedText name, bool isTemporal)
    {
        Code = code.Value;
        NameL10n = name;
        IsTemporal = isTemporal;
        SourceKind = RegistrySourceKind.Local;
        DataRevision = 0;
        DefinitionVersion = 1;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Чи мають записи період дії (резолвінг «станом на дату періоду»).</summary>
    public bool IsTemporal { get; private set; }

    /// <summary>Хто master: зовнішня система, гібрид або ECR (ФВ-8.9).</summary>
    public RegistrySourceKind SourceKind { get; private set; }

    /// <summary>
    /// Ревізія **даних**. Входить у ключ кешу списків: без неї кеш або
    /// застаріває після синку, або потребує інвалідації між інстансами.
    /// </summary>
    public int DataRevision { get; private set; }

    /// <summary>Версія **визначення** (склад полів).</summary>
    public int DefinitionVersion { get; private set; }

    public bool IsActive { get; private set; }
    public IReadOnlyList<RegistryFieldDef> Fields => _fields;

    /// <summary>Інкремент ревізії даних після зміни записів.</summary>
    public void BumpDataRevision() => DataRevision++;

    /// <summary>
    /// Інкремент версії <b>визначення</b> — складу полів і правил (ФВ-8.12).
    /// </summary>
    /// <remarks>
    /// ⛔ Окремо від <see cref="BumpDataRevision"/>, і це не дублювання: перша
    /// росте від зміни ЗАПИСІВ і входить у ключ кешу списків, друга — від
    /// зміни ОПИСУ. Змішати їх означало б скидати кеш тисяч записів на кожне
    /// перейменування поля і не скидати нічого там, де поле з'явилося.
    /// </remarks>
    public void BumpDefinitionVersion() => DefinitionVersion++;

    /// <summary>Додає поле до опису довідника.</summary>
    /// <param name="field">Поле; має належати цьому ж довіднику.</param>
    /// <exception cref="DomainException">
    /// Поле належить іншому довіднику або код повторюється — <c>ECR-REG-0422</c>.
    /// </exception>
    public void AddField(RegistryFieldDef field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.RegistryDefId != Id)
        {
            throw new DomainException(
                "ECR-REG-0422",
                $"Поле «{field.Code}» належить довіднику {field.RegistryDefId}, а не {Id}.");
        }

        // ⚠ Дубль коду ловиться тут, а не лише унікальним індексом. Індекс
        // віддав би помилку провайдера на SaveChanges — тобто після того, як
        // решта змін набору вже пройшла валідацію, і без назви поля в тексті.
        if (_fields.Exists(f => string.Equals(f.Code, field.Code, StringComparison.OrdinalIgnoreCase)))
        {
            throw new DomainException(
                "ECR-REG-0422", $"Поле з кодом «{field.Code}» у довіднику «{Code}» вже є.");
        }

        _fields.Add(field);
    }

    /// <summary>
    /// Перемикає master. Дозволено **лише поза відкритим періодом** —
    /// перевірка виконується в use-case, тут лише зміна стану (ФВ-8.9).
    /// </summary>
    public void SwitchSource(RegistrySourceKind kind) => SourceKind = kind;
}
