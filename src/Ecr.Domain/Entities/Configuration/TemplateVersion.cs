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
        => throw new NotImplementedException(
            "TODO: перевірити Status == Draft (інакше DomainException ECR-TMPL-0409); " +
            "виставити Status = Published, PublishedAt = utcNow, PublishedByUserId; " +
            "PresentationRevision лишити 0.");

    /// <summary>
    /// Реєструє презентаційну правку. Викликається <b>після</b> успішного
    /// оновлення в БД, значення береться з <c>OUTPUT</c> (R-B7).
    /// </summary>
    public void ApplyPresentationRevision(int newRevision)
        => throw new NotImplementedException(
            "TODO: перевірити newRevision == PresentationRevision + 1; інакше DomainException " +
            "(розбіжність означає паралельну правку, яку ми пропустили); присвоїти значення.");

    /// <summary>Перевіряє, чи допустима структурна зміна в поточному стані.</summary>
    /// <exception cref="DomainException">Версія структурно заморожена.</exception>
    public void EnsureStructurallyMutable()
        => throw new NotImplementedException(
            "TODO: якщо IsStructurallyFrozen — кинути DomainException з кодом ECR-TMPL-0409 " +
            "і поясненням, що структурні зміни робляться через CloneFrom (ФВ-7.1).");
}
