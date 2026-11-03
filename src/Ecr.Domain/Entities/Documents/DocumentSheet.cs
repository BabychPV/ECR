using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Включення аркуша до складу документа (ФВ-3.2).</summary>
/// <remarks>
/// RS (рішення людини 03.11): рядок із <see cref="CopyOfId"/> — КОПІЯ аркуша «&lt;Назва&gt; N». Джерело копії —
/// завжди оригінал (<see cref="CopyOfId"/> = <c>null</c>, <see cref="CopyOrdinal"/> = 1); копія порожня, з тією самою
/// структурою і формулами, існує в усіх періодах і має власний життєвий цикл за періодом. Дані копії розрізняє
/// <c>SheetCopyId</c> = <see cref="Entity{TKey}.Id"/> копії (<c>doc.TableInstance</c>, <c>wf.ApprovalState</c>,
/// <c>wf.ApprovalEvent</c>, <c>calc.SubmissionSnapshot</c>); <c>NULL</c> там — оригінал.
/// </remarks>
public sealed class DocumentSheet : Entity<long>
{
    /// <summary>Порядковий номер оригіналу в групі копій.</summary>
    public const short OriginalOrdinal = 1;

    private DocumentSheet() { }

    public DocumentSheet(long documentId, int sheetDefId)
    {
        DocumentId = documentId;
        SheetDefId = sheetDefId;
        IsIncluded = true;
        CopyOrdinal = OriginalOrdinal;
    }

    public long DocumentId { get; private set; }
    public int SheetDefId { get; private set; }
    public bool IsIncluded { get; private set; }

    /// <summary>Оригінал, копією якого є цей рядок; <c>null</c> — сам оригінал.</summary>
    public long? CopyOfId { get; private set; }

    /// <summary>Номер у групі: 1 — оригінал, 2… — копії («&lt;Назва&gt; N»).</summary>
    public short CopyOrdinal { get; private set; }

    /// <summary>Назва групи, задана в документі (лише на оригіналі); <c>null</c> — назва аркуша шаблону.</summary>
    public LocalizedText? NameOverrideL10n { get; private set; }

    /// <summary>Оптимістичне блокування: дві одночасні правки групи (перенумерація, назва) не змішуються.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Чи це копія, а не оригінал.</summary>
    public bool IsCopy => CopyOfId is not null;

    /// <summary>Нова порожня копія цього (оригінального) аркуша з номером <paramref name="ordinal"/>.</summary>
    /// <param name="ordinal">Номер копії в групі, ≥ 2.</param>
    /// <exception cref="InvalidOperationException">Аркуш сам є копією, ще не збережений або номер &lt; 2.</exception>
    /// <remarks>⛔ Джерело копії — лише оригінал (рішення людини 03.11): копія копії не заводиться.</remarks>
    public DocumentSheet CreateCopy(short ordinal)
    {
        if (IsCopy)
        {
            throw new InvalidOperationException("Копію заводять лише з оригінального аркуша.");
        }

        if (!IsPersisted)
        {
            throw new InvalidOperationException("Оригінал аркуша ще не збережено: копії нема на що посилатися.");
        }

        RequireCopyOrdinal(ordinal);

        return new DocumentSheet(DocumentId, SheetDefId) { CopyOfId = Id, CopyOrdinal = ordinal };
    }

    /// <summary>Перейменовує групу аркушів документа (лише оригінал): усі копії стають «&lt;Назва&gt; N».</summary>
    /// <param name="name">Нова назва; <c>null</c> — повернути назву з шаблону.</param>
    /// <exception cref="InvalidOperationException">Рядок — копія.</exception>
    public void Rename(LocalizedText? name)
    {
        if (IsCopy)
        {
            throw new InvalidOperationException("Назву групи змінюють на оригінальному аркуші.");
        }

        NameOverrideL10n = name;
    }

    /// <summary>Перенумеровує копію після видалення іншої копії групи.</summary>
    /// <param name="ordinal">Новий номер, ≥ 2.</param>
    /// <exception cref="InvalidOperationException">Рядок — оригінал або номер &lt; 2.</exception>
    public void Renumber(short ordinal)
    {
        if (!IsCopy)
        {
            throw new InvalidOperationException("Оригінал аркуша завжди має номер 1.");
        }

        RequireCopyOrdinal(ordinal);
        CopyOrdinal = ordinal;
    }

    private static void RequireCopyOrdinal(short ordinal)
    {
        if (ordinal <= OriginalOrdinal)
        {
            throw new InvalidOperationException($"Номер копії аркуша має бути ≥ 2, а не {ordinal}.");
        }
    }
}
