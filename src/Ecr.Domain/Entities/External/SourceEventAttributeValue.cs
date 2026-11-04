// src/Ecr.Domain/Entities/External/SourceEventAttributeValue.cs
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Значення атрибута події PI за ОСТАННІМ прочитанням синхронізацією (<c>ext.SourceEventAttributeValue</c>,
/// <c>D-465</c>) — те, що «Дані з PI» і «Звідки це значення» показують поруч із коміркою рядка події.
/// </summary>
/// <remarks>
/// ⛔ Пише лише <c>SourceEventSyncJob</c> — одним set-based <c>MERGE</c> на сторінку зв'язків і ЛИШЕ ЗМІНЕНЕ
/// (<see cref="ValueHash"/>): щопрогінне перечитування всього річного періоду не переписує незмінених рядків.
/// Читає перегляд; у PI наживо перегляд не ходить (недоступний PI не ламає екран, а кожне відкриття не
/// навантажує PI замовника).
/// <para>
/// ⚠ Ключ — зв'язок (<see cref="LinkId"/>, <c>ext.SourceEventLink.Id</c>) + область + атрибут. Зовнішнього ключа
/// на зв'язок НЕМАЄ: усі ключі моделі — <c>Restrict</c> (<c>DeleteBehaviorTests</c>), а зв'язок видаляє синк
/// (повна звірка, передача братньому шаблону) — значення знімаються тим самим кроком (той самий ADO-коміт для
/// видалення з рядком; той самий прогін — для зв'язку без рядка). Таблиця не партиційована й не посилається
/// на <c>doc.*</c>, тож архівація (<c>TRUNCATE … WITH (PARTITIONS)</c>) її не зачіпає.
/// </para>
/// </remarks>
public sealed class SourceEventAttributeValue
{
    /// <summary>Довжина тексту значення в сховищі; довше обрізається з <see cref="IsTruncated"/>.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>Довжина символу одиниці джерела.</summary>
    public const int MaxUnitLength = 64;

    private SourceEventAttributeValue() { }

    /// <summary>Зв'язок «подія ↔ рядок» (<c>ext.SourceEventLink.Id</c>).</summary>
    public long LinkId { get; private set; }

    /// <summary>Де лежить атрибут.</summary>
    public SourceEventAttributeScope Scope { get; private set; }

    /// <summary>Ім'я атрибута, як його повернуло джерело.</summary>
    public string Attribute { get; private set; } = null!;

    /// <summary>Число в одиниці джерела.</summary>
    public decimal? ValueNumeric { get; private set; }

    /// <summary>Текст (до <see cref="MaxTextLength"/>).</summary>
    public string? ValueText { get; private set; }

    /// <summary>Текст довший за сховище — збережено початок.</summary>
    public bool IsTruncated { get; private set; }

    /// <summary>UOM джерела; <c>null</c> — не назване.</summary>
    public string? SourceUnit { get; private set; }

    /// <summary>SHA-256 канонічного вигляду значення — ключ «змінилося / ні».</summary>
    public byte[] ValueHash { get; private set; } = [];

    /// <summary><c>Modified</c> події в джерелі на момент прочитання цього значення.</summary>
    public DateTime? SourceModifiedUtc { get; private set; }

    /// <summary>Коли синк уперше прочитав ЦЕ значення (UTC); незмінне значення його не зсуває.</summary>
    public DateTime ChangedAt { get; private set; }

    /// <summary>Прогін синку, що прочитав це значення.</summary>
    public string RunId { get; private set; } = null!;
}
