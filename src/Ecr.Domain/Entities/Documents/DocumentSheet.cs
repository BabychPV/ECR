using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Включення аркуша до складу документа (ФВ-3.2).</summary>
public sealed class DocumentSheet : Entity<long>
{
    private DocumentSheet() { }

    public DocumentSheet(long documentId, int sheetDefId)
    {
        DocumentId = documentId;
        SheetDefId = sheetDefId;
        IsIncluded = true;
    }

    public long DocumentId { get; private set; }
    public int SheetDefId { get; private set; }
    public bool IsIncluded { get; private set; }
}
