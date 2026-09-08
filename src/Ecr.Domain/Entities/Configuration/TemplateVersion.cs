using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Версія шаблону. Після <see cref="Publish"/> **структурно незмінна**:
/// змінюється лише презентаційний шар, і кожна така зміна інкрементує
/// <see cref="PresentationRevision"/>.
/// </summary>
/// <remarks>
/// Саме ця незмінність робить ключ кешу <c>v{id}:r{rev}</c> самодостатнім і
/// прибирає когерентність кешу між інстансами як клас проблеми (D-16).
/// </remarks>
public sealed class TemplateVersion : Entity<int>
{
    private readonly List<SheetDef> _sheets = [];

    private TemplateVersion() { }

    /// <summary>Створює чернетку версії.</summary>
    public TemplateVersion(int templateId, string version, int createdByUserId, DateTime utcNow, int? clonedFromVersionId = null)
    {
        TemplateId = templateId;
        Version = version;
        Status = TemplateVersionStatus.Draft;
        ClonedFromVersionId = clonedFromVersionId;
        PresentationRevision = 0;
        CreatedAt = utcNow;
        CreatedByUserId = createdByUserId;
    }

    public int TemplateId { get; private set; }
    public string Version { get; private set; } = null!;
    public TemplateVersionStatus Status { get; private set; }
    public int? ClonedFromVersionId { get; private set; }

    /// <summary>Ревізія презентаційного шару. Інкрементує застосунок одним statement (R-B7).</summary>
    public int PresentationRevision { get; private set; }

    public byte[]? SourceWorkbookHash { get; private set; }
    public DateTime? PublishedAt { get; private set; }
    public int? PublishedByUserId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public int CreatedByUserId { get; private set; }

    public IReadOnlyList<SheetDef> Sheets => _sheets;

    /// <summary>Чи заборонені структурні зміни.</summary>
    public bool IsStructurallyFrozen => Status is TemplateVersionStatus.Published or TemplateVersionStatus.Deprecated;

    /// <summary>Ключ кешу метаданих.</summary>
    public string CacheKey => $"v{Id}:r{PresentationRevision}";

    /// <summary>
    /// Публікує версію. Валідація цілісності виконується <b>до</b> виклику
    /// (ФВ-2.9) — тут лише перехід стану.
    /// </summary>
    /// <exception cref="DomainException">Версія вже опублікована.</exception>
    public void Publish(int publishedByUserId, DateTime utcNow)
    {
        if (Status != TemplateVersionStatus.Draft)
        {
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Версію {Version} вже опубліковано або виведено з обігу (стан {Status}). " +
                "Повторна публікація неможлива: опублікована версія структурно незмінна, " +
                "а зміни вносяться клонуванням у нову версію (ФВ-7.1).");
        }

        Status = TemplateVersionStatus.Published;
        PublishedAt = utcNow;
        PublishedByUserId = publishedByUserId;

        // PresentationRevision навмисно лишається 0: публікація не є
        // презентаційною правкою, і ключ кешу свіжоопублікованої версії має
        // бути v{id}:r0.
    }

    /// <summary>
    /// Реєструє презентаційну правку. Викликається <b>після</b> успішного
    /// оновлення в БД, значення береться з <c>OUTPUT</c> (R-B7).
    /// </summary>
    public void ApplyPresentationRevision(int newRevision)
    {
        if (newRevision != PresentationRevision + 1)
        {
            // Розбіжність означає, що між читанням і записом хтось інший уже
            // інкрементував ревізію. Прийняти це значення — означало б
            // видати за поточний стан той, якого ми не бачили, і ключ кешу
            // почав би вказувати на неактуальну структуру.
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Очікувалася презентаційна ревізія {PresentationRevision + 1}, отримано {newRevision}. " +
                "Розбіжність означає паралельну правку, яку ця сесія не бачила.");
        }

        PresentationRevision = newRevision;
    }

    /// <summary>
    /// Виводить версію з обігу (<c>ФВ-7.8</c>): відкат без видалення.
    /// </summary>
    /// <remarks>
    /// ⛔ Версія **не видаляється**. На неї посилаються проєкти, подані форми,
    /// зрізи звітності й аудит структурних змін; видалення розірвало б цей
    /// ланцюг, а зміст відкату — «більше не використовувати», а не «стерти
    /// сліди».
    ///
    /// ⛔ Стан <c>Deprecated</c> існував від Етапу 1 і був **недосяжним**:
    /// <c>IsStructurallyFrozen</c> його враховував, публікація на нього
    /// посилалася у тексті відмови, а перевести версію в нього не міг ніхто.
    /// Тобто відкат опублікованої версії був неможливий у принципі.
    ///
    /// ⚠ Структурна заморозка ЛИШАЄТЬСЯ: виведена з обігу версія так само
    /// незмінна. Проєкти, прив'язані до неї, працюють далі — інакше відкат
    /// зупинив би заповнення форм посеред періоду (<c>ФВ-1.2</c>).
    /// </remarks>
    /// <param name="deprecatedByUserId">Хто виводить з обігу.</param>
    /// <param name="utcNow">Момент операції.</param>
    /// <exception cref="DomainException">Версія не опублікована.</exception>
    public void Deprecate(int deprecatedByUserId, DateTime utcNow)
    {
        if (Status != TemplateVersionStatus.Published)
        {
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Вивести з обігу можна лише опубліковану версію; поточний стан {Status}. " +
                "Чернетку виводити нема від чого — вона ще нікуди не потрапила.");
        }

        Status = TemplateVersionStatus.Deprecated;
        DeprecatedAt = utcNow;
        DeprecatedByUserId = deprecatedByUserId;
    }

    /// <summary>Момент виведення з обігу; <c>null</c> — версія в обігу.</summary>
    public DateTime? DeprecatedAt { get; private set; }

    /// <summary>Хто вивів версію з обігу.</summary>
    public int? DeprecatedByUserId { get; private set; }

    /// <summary>Перевіряє, чи допустима структурна зміна в поточному стані.</summary>
    /// <exception cref="DomainException">Версія структурно заморожена.</exception>
    public void EnsureStructurallyMutable()
    {
        if (IsStructurallyFrozen)
        {
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Версія {Version} у стані {Status} структурно заморожена. " +
                "Структурні зміни вносяться клонуванням у нову версію (ФВ-7.1); " +
                "без клону вже подані документи мовчки змінили б свою структуру.");
        }
    }

    /// <summary>Додає аркуш до версії.</summary>
    /// <remarks>
    /// ⚠ Перевірка коду — за зразком <see cref="SheetDef.AddTable"/>: код
    /// аркуша — це його ідентичність і адреса в <c>PUT …/sheets/{code}</c>, а
    /// не лише етикетка, тому дублікат тут — помилка виклику, а не деталь
    /// подання.
    /// </remarks>
    /// <exception cref="DomainException">Аркуш із таким кодом уже є у версії.</exception>
    public void AddSheet(SheetDef sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (_sheets.Any(s => string.Equals(s.Code, sheet.Code, StringComparison.Ordinal)))
        {
            throw new DomainException(
                "ECR-TMPL-0409",
                $"Аркуш з кодом {sheet.Code} у версії {Version} уже існує: код — це ідентичність, " +
                "на нього посилається адреса PUT-запиту.");
        }

        _sheets.Add(sheet);
    }
}
