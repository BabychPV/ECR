// src/Ecr.Domain/Entities/External/RawData.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Сира точка з джерела (<c>ext.RawDataPoint</c>).
/// </summary>
/// <remarks>
/// ⛔ Значення зберігається **в одиниці ДЖЕРЕЛА** (ФВ-16.10, D-79). Конверсія
/// робиться при завантаженні в документ і журналюється; конвертувати «на
/// вході» означало б, що повторний перерахунок з архіву дасть інший
/// результат, якщо мапінг одиниць за цей час змінили — і ніхто не зможе
/// сказати, яке число було правильним.
/// <para>
/// Ключ унікальності — природний (<c>сутність × шлях × час</c>), і це те, що
/// робить збір **ідемпотентним** (ФВ-11.3): повторний прогін того самого
/// вікна не подвоює точки, тому наздоганяння безпечне.
/// </para>
/// </remarks>
public sealed class RawDataPoint : Entity<long>
{
    private RawDataPoint() { }

    /// <summary>Створює сиру точку.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourcePath">Шлях атрибута в джерелі.</param>
    /// <param name="timestamp">Мітка часу точки.</param>
    /// <param name="collectionRunId">Прогін, що її приніс.</param>
    /// <param name="retrievedAt">Коли отримано.</param>
    public RawDataPoint(
        int sourceEntityId,
        string sourcePath,
        DateTime timestamp,
        long collectionRunId,
        DateTime retrievedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        SourceEntityId = sourceEntityId;
        SourcePath = sourcePath;
        Timestamp = timestamp;
        CollectionRunId = collectionRunId;
        RetrievedAt = retrievedAt;
    }

    public int SourceEntityId { get; private set; }
    public string SourcePath { get; private set; } = null!;
    public DateTime Timestamp { get; private set; }

    /// <summary><c>decimal(28,10)</c>; <c>float</c> заборонений (D-30).</summary>
    public decimal? ValueNumeric { get; private set; }

    public string? ValueString { get; private set; }

    /// <summary>Одиниця ДЖЕРЕЛА, не цільова.</summary>
    public int? UnitId { get; private set; }

    /// <summary>Якість точки за класифікацією джерела; <c>null</c> — не повідомлена.</summary>
    /// <remarks>
    /// Зберігається як є, а не тлумачиться: «сумнівна» точка PI AF лишається
    /// сумнівною і в ECR, і рішення про неї ухвалює методологія, а не збір.
    /// </remarks>
    public string? Quality { get; private set; }

    public DateTime RetrievedAt { get; private set; }
    public long CollectionRunId { get; private set; }

    /// <summary>Записує значення точки.</summary>
    /// <param name="valueNumeric">Число.</param>
    /// <param name="valueString">Текст для нечислових атрибутів.</param>
    /// <param name="unitId">Одиниця джерела.</param>
    /// <param name="quality">Якість за класифікацією джерела.</param>
    public void SetValue(decimal? valueNumeric, string? valueString, int? unitId, string? quality)
    {
        ValueNumeric = valueNumeric;
        ValueString = valueString;
        UnitId = unitId;
        Quality = quality;
    }
}

/// <summary>
/// Правило звіряння даних джерела (<c>ext.ConsistencyRule</c>).
/// </summary>
/// <remarks>
/// Правила — **дані**, а не код: набір перевірок змінюється конфігуратором.
/// Знахідка нічної перевірки — **баг, а не шум** (ФВ-7.7): якщо перевірка
/// регулярно щось знаходить і це вважають нормою, вона перестає працювати як
/// сигнал.
/// </remarks>
public sealed class ConsistencyRule : Entity<int>
{
    private ConsistencyRule() { }

    /// <summary>Створює правило звіряння.</summary>
    /// <param name="code">Код правила; входить у повідомлення про знахідку.</param>
    /// <param name="expression">Умова, за якої дані вважаються справними.</param>
    /// <param name="severity">Вага знахідки.</param>
    /// <param name="sourceEntityId">Сутність джерела; <c>null</c> — правило спільне.</param>
    public ConsistencyRule(string code, string expression, byte severity, int? sourceEntityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        Code = code;
        Expression = expression;
        Severity = severity;
        SourceEntityId = sourceEntityId;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public int? SourceEntityId { get; private set; }
    public string Expression { get; private set; } = null!;
    public byte Severity { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Вимикає правило.</summary>
    public void Deactivate() => IsActive = false;
}
