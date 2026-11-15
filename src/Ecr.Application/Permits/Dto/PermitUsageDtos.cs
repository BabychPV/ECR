// src/Ecr.Application/Permits/Dto/PermitUsageDtos.cs
namespace Ecr.Application.Permits.Dto;

/// <summary>Види посилань документа на дозвіл (<see cref="PermitUsageItemDto.Kind"/>).</summary>
public static class PermitUsageKinds
{
    /// <summary>Поле шапки документа типу <c>Lookup</c> вказує на дозвіл.</summary>
    public const string Header = "header";

    /// <summary>Комірка рядка таблиці (колонка <c>Lookup</c>) вказує на дозвіл.</summary>
    public const string Cell = "cell";
}

/// <summary>Одне місце, де дозвіл стоїть значенням: поле шапки або комірка рядка таблиці документа.</summary>
/// <param name="Kind"><c>header</c> або <c>cell</c> (<see cref="PermitUsageKinds"/>).</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="ProjectId">Проєкт документа.</param>
/// <param name="ProjectCode">Код проєкту.</param>
/// <param name="PeriodKey">Період комірки (<c>YYYYMM</c>-подібний ключ); <c>null</c> для шапки - вона належить документу цілком.</param>
/// <param name="SheetCode">Код аркуша; <c>null</c> для шапки.</param>
/// <param name="SheetNameL10n">Назва аркуша мовами каталогу; <c>null</c> для шапки.</param>
/// <param name="TableCode">Код таблиці; <c>null</c> для шапки.</param>
/// <param name="TableNameL10n">Назва таблиці мовами каталогу; <c>null</c> для шапки.</param>
/// <param name="RowKey">Бізнес-ключ рядка; <c>null</c> для шапки.</param>
/// <param name="RowOrdinal">Порядок рядка на екрані; <c>null</c> для шапки.</param>
/// <param name="FieldCode">Код поля шапки або колонки.</param>
/// <param name="FieldLabelL10n">Підпис поля шапки або заголовок колонки мовами каталогу.</param>
public sealed record PermitUsageItemDto(
    string Kind,
    long DocumentId,
    string DocumentKey,
    int ProjectId,
    string ProjectCode,
    int? PeriodKey,
    string? SheetCode,
    Dictionary<string, string>? SheetNameL10n,
    string? TableCode,
    Dictionary<string, string>? TableNameL10n,
    string? RowKey,
    int? RowOrdinal,
    string FieldCode,
    Dictionary<string, string> FieldLabelL10n);

/// <summary>
/// «Де використано» дозвіл: документи, у яких він обраний значенням поля шапки чи колонки таблиці або стоїть в історії
/// дозволів документа (B56). Лише документи проєктів, які читач бачить (<c>Document.View</c>); решта не рахується й не
/// називається. Логічно видалені рядки (і їхня історія) не рахуються.
/// </summary>
/// <param name="PermitId">Дозвіл.</param>
/// <param name="DocumentCount">
/// Скільки різних документів посилаються на дозвіл (під фільтром періоду): полем шапки, коміркою або історією дозволів
/// (пізніший дозвіл «з дати», частина місяця). Документ рахується один раз, скільки б місць чи сегментів історії в ньому не було
/// (B56).
/// </param>
/// <param name="LinkCount">Скільки місць (полів шапки й комірок) посилаються на дозвіл; історія дозволів сюди не входить.</param>
/// <param name="Truncated"><c>true</c>, якщо <paramref name="Items"/> обрізано лімітом <c>limit</c>.</param>
/// <param name="Items">Місця посилань: за документом, періодом, шапка перед таблицями.</param>
/// <param name="HistoryDocumentCount">
/// Скільки з <paramref name="DocumentCount"/> документів мають дозвіл у записах історії дозволів: сегмент «з дати» поля
/// шапки чи живого рядка, або частина поділеного місяця. ⚠ Частина 1 поділеного місяця несе БАЗОВИЙ дозвіл комірки, тож
/// документ, де дозвіл стоїть значенням комірки з поділеним місяцем, теж потрапляє сюди — це «є в історії», а не «лише в
/// історії». Записи історії не перелічуються в <paramref name="Items"/> і не входять у <c>LinkCount</c>; документ, де дозвіл є
/// лише в історії, входить у <paramref name="DocumentCount"/>, але не має рядка в переліку. З фільтром періоду: частина — свого
/// місяця, сегмент — якщо документ має таблиці в періоді і сегмент чинний не пізніше кінця періоду (B56).
/// </param>
public sealed record PermitUsageDto(
    long PermitId,
    int DocumentCount,
    int LinkCount,
    bool Truncated,
    IReadOnlyList<PermitUsageItemDto> Items,
    int HistoryDocumentCount = 0);
