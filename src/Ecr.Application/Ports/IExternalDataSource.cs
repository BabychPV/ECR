// src/Ecr.Application/Ports/IExternalDataSource.cs
namespace Ecr.Application.Ports;

using Ecr.Domain.Enums;

/// <summary>
/// Читання із зовнішнього джерела. PI AF — <b>виключно джерело</b>: система в
/// нього нічого не пише (D-44), тому парного <c>IExternalDataSink</c> не існує.
/// </summary>
public interface IExternalDataSource
{
    /// <summary>Транспорт, який реалізує адаптер.</summary>
    ExternalTransport Transport { get; }

    /// <summary>Каталог сутностей джерела — для конфігуратора, щоб не вводити імена руками.</summary>
    Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct);

    /// <summary>
    /// Читає діапазон. Ідемпотентно: повторний запуск того самого діапазону не
    /// дублює даних (ФВ-11.3).
    /// </summary>
    Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────────────────────
// Типи, яких у пакеті не було (Q-014). Чернетка на затвердження.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Елемент каталогу джерела — те, що конфігуратор бачить у списку і з чого
/// створює <c>ext.SourceEntity</c>.
/// </summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове</b> (спершу «слабке»; уточнено після
/// рев'ю Етапу 0). Поля дослівно відповідають колонкам <c>ext.SourceEntity</c>
/// (<c>02a</c> рядок 1342): <c>Code</c>, <c>DisplayName</c>, <c>EntityPath</c> —
/// саме їх заповнює «каталог сутностей джерела — для конфігуратора, щоб не
/// вводити імена руками». Адаптер при цьому <b>не створює артефактів у базі
/// джерела</b> (ФВ-11.2): <c>Discover</c> лише читає.
/// <see cref="SourceUnitSymbol"/> додано мною, бо обидві реалізації
/// <c>DiscoverAsync</c> у своїх <c>TODO</c> пишуть «збирати атрибути
/// <b>з їхнім UOM</b>»: одиниця джерела — «найчастіше джерело мовчазних
/// розбіжностей у числах» (ФВ-16.9), і побачити її треба вже в каталозі.
/// <c>Id</c>, <c>DataSourceId</c>, <c>RegistryDefId</c>, <c>IsActive</c> сюди
/// не входять: це наші поля, а не поля джерела.
/// </remarks>
/// <param name="Code">Унікальний у межах джерела код.</param>
/// <param name="DisplayName">Людська назва.</param>
/// <param name="EntityPath">Шлях в ієрархії AF.</param>
/// <param name="SourceUnitSymbol">UOM атрибута в термінах джерела; <c>null</c> — безрозмірний.</param>
/// <param name="DataType">Тип значення в термінах джерела.</param>
public sealed record SourceEntityDescriptor(
    string Code,
    string? DisplayName,
    string? EntityPath,
    string? SourceUnitSymbol,
    string? DataType);

/// <summary>Запит на читання діапазону з джерела.</summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — слабке (здогадка).</b> Форма виведена з
/// <c>CollectionRunner.RunAsync(int sourceEntityId, DateTime from, DateTime to, …)</c>
/// і з таблиці <c>itg.CollectionRun</c> (<c>SourceEntityId</c>,
/// <c>RangeFrom</c>, <c>RangeTo</c>). <see cref="SourcePath"/> потрібен, бо
/// природний ключ <c>ext.RawDataPoint</c> — це
/// <c>(SourceEntityId, SourcePath, Timestamp)</c>, а одна сутність джерела може
/// мати кілька атрибутів. <see cref="MaxPoints"/> додано мною: обидві
/// реалізації <c>ReadAsync</c> у <c>TODO</c> вимагають «батчі обмеженого розміру».
/// </remarks>
/// <param name="DataSourceId">Джерело — визначає транспорт і облікові дані.</param>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="SourcePath">Шлях атрибута; частина природного ключа точки.</param>
/// <param name="FromUtc">Початок діапазону, включно.</param>
/// <param name="ToUtc">Кінець діапазону, виключно.</param>
/// <param name="MaxPoints">Обмеження розміру батча.</param>
public sealed record CollectionRequest(
    int DataSourceId,
    int SourceEntityId,
    string SourcePath,
    DateTime FromUtc,
    DateTime ToUtc,
    int MaxPoints);

/// <summary>Прочитане з джерела плюс те, що прочитати не вдалося.</summary>
/// <remarks>
/// ⚠ <b>Q-014, обґрунтування — часткове.</b> Наявність
/// <see cref="FailedIntervals"/> — не прикраса, а пряма вимога <c>TODO</c>
/// обох реалізацій: «часткова відмова батча — це НЕ загальний провал: успішні
/// точки зберегти, невдалі повернути в catch-up». Без цього поля адаптер може
/// повідомити лише «все добре» або «все погано», і журнал покриття
/// (<c>itg.CollectionCoverage</c>) стане неправдивим.
/// </remarks>
/// <param name="Points">Точки в <b>одиниці джерела</b> (ФВ-16.9).</param>
/// <param name="FailedIntervals">Інтервали, які треба дозібрати.</param>
/// <param name="ErrorCode">Код помилки джерела (<c>ECR-INT-0503</c>); <c>null</c> — відмов не було.</param>
public sealed record CollectionResult(
    IReadOnlyList<SourceDataPoint> Points,
    IReadOnlyList<TimeInterval> FailedIntervals,
    string? ErrorCode);

/// <summary>Одна прочитана точка — рядок <c>ext.RawDataPoint</c> до збереження.</summary>
/// <param name="SourcePath">Шлях атрибута.</param>
/// <param name="Timestamp">Мітка часу точки.</param>
/// <param name="ValueNumeric">Числове значення в одиниці джерела.</param>
/// <param name="ValueString">Текстове значення для нечислових тегів.</param>
/// <param name="SourceUnitSymbol">UOM джерела; конверсія — на межі, із записом у журнал.</param>
/// <param name="Quality">Якість у термінах джерела.</param>
public sealed record SourceDataPoint(
    string SourcePath,
    DateTime Timestamp,
    decimal? ValueNumeric,
    string? ValueString,
    string? SourceUnitSymbol,
    string? Quality);

/// <summary>Часовий інтервал; кінець виключно.</summary>
public sealed record TimeInterval(DateTime FromUtc, DateTime ToUtc);
