using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Документ — екземпляр звітності; аналог «HSE-файлу» чинного рішення.</summary>
public sealed class Document : Entity<long>
{
    private readonly List<DocumentSheet> _sheets = [];

    private Document() { }

    public Document(int projectId, string businessKey, int createdByUserId, DateTime utcNow)
    {
        ProjectId = projectId;
        BusinessKey = businessKey;
        CreatedAt = utcNow;
        CreatedByUserId = createdByUserId;
        ModifiedAt = utcNow;
        ModifiedByUserId = createdByUserId;
    }

    public int ProjectId { get; private set; }

    /// <summary>Складається зі значень колонок із <c>IsBusinessKey</c>.</summary>
    public string BusinessKey { get; private set; } = null!;

    public LocalizedText? NameL10n { get; private set; }

    // ⛔ Статусу документа тут НЕМАЄ (D-93). Гранулярність затвердження —
    // аркуш × період (D-38), і єдине джерело істини — wf.ApprovalState.
    // Скалярний Status тут був би другим джерелом, яке рано чи пізно
    // розійдеться з першим і покаже «Approved» на документі, половина
    // аркушів якого ще в Draft. Потрібен зведений стан для списку
    // документів — рахуйте його запитом до wf.ApprovalState, не колонкою.

    public DateTime CreatedAt { get; private set; }
    public int CreatedByUserId { get; private set; }
    public DateTime ModifiedAt { get; private set; }
    public int ModifiedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<DocumentSheet> Sheets => _sheets;

    /// <summary>Фіксує зміну документа.</summary>
    public void Touch(int userId, DateTime utcNow)
    {
        ModifiedAt = utcNow;
        ModifiedByUserId = userId;
    }
}
