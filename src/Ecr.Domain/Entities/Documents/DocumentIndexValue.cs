// src/Ecr.Domain/Entities/Documents/DocumentIndexValue.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Індексоване значення документа для фільтрів (<c>doc.DocumentIndexValue</c>).
/// </summary>
/// <remarks>
/// ⚠ Існує тому, що <c>doc.CellValue</c> не має **жодного** некластерного
/// індексу: при ~108 млн рядків ширший ключ у некластерному коштував би
/// більше, ніж дає. Усі альтернативні доступи — «знайти документи, де
/// <c>Permit = P-001</c>» — ідуть сюди.
/// <para>
/// Наповнює шлях ЗАПИСУ: значення потрапляє в індекс тією самою транзакцією,
/// що й у комірку. Нічна перебудова означала б, що фільтр показує вчорашній
/// стан — і користувач не знайде документа, який щойно заповнив.
/// </para>
/// </remarks>
public sealed class DocumentIndexValue : Entity<long>
{
    private DocumentIndexValue() { }

    /// <summary>Створює індексоване значення.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="columnDefId">Колонка з <c>IsIndexed = 1</c>.</param>
    public DocumentIndexValue(long documentId, int columnDefId)
    {
        DocumentId = documentId;
        ColumnDefId = columnDefId;
    }

    public long DocumentId { get; private set; }
    public int ColumnDefId { get; private set; }

    public string? ValueString { get; private set; }

    /// <summary><c>decimal(28,10)</c>; <c>float</c> заборонений (D-30).</summary>
    public decimal? ValueNumeric { get; private set; }

    public DateTime? ValueDate { get; private set; }

    /// <summary>Записує значення для фільтра.</summary>
    /// <param name="valueString">Текст.</param>
    /// <param name="valueNumeric">Число.</param>
    /// <param name="valueDate">Дата.</param>
    public void SetValue(string? valueString, decimal? valueNumeric, DateTime? valueDate)
    {
        ValueString = valueString;
        ValueNumeric = valueNumeric;
        ValueDate = valueDate;
    }
}
