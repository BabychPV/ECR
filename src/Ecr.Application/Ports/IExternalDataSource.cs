// src/Ecr.Application/Ports/IExternalDataSource.cs

using Ecr.Application.Errors;
using Ecr.Application.Sources;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання із зовнішнього джерела. PI AF — <b>виключно джерело</b>: система в
/// нього нічого не пише (D-44), тому парного <c>IExternalDataSink</c> не існує.
/// </summary>
public interface IExternalDataSource
{
    /// <summary>Транспорт, який реалізує адаптер.</summary>
    public ExternalTransport Transport { get; }

    /// <summary>Каталог сутностей джерела — для конфігуратора, щоб не вводити імена руками.</summary>
    public Task<IReadOnlyList<SourceEntityDescriptor>> DiscoverAsync(int dataSourceId, CancellationToken ct);

    /// <summary>
    /// Читає діапазон. Ідемпотентно: повторний запуск того самого діапазону не
    /// дублює даних (ФВ-11.3).
    /// </summary>
    public Task<CollectionResult> ReadAsync(CollectionRequest request, CancellationToken ct);

    /// <summary>
    /// Згортає одне вікно <c>[FromUtc, ToUtc)</c> у число (HSE301 §4.3, <c>D-172</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Типова реалізація — <b>локальна</b>: сирі точки з запасом на межах
    /// через <see cref="ReadAsync"/> і згортка <see cref="WindowFold"/>, тобто
    /// тим самим <see cref="PeriodFold"/>, що й місячна матеріалізація. Адаптер
    /// перевизначає метод, лише якщо вміє summary на сервері й це налаштовано
    /// (<see cref="WindowComputedBy.Server"/>). Чинні реалізації не змінюються.
    /// </remarks>
    /// <param name="request">Вікно, атрибут і спосіб згортки.</param>
    /// <param name="ct">Скасування.</param>
    public Task<WindowResult> ReadWindowAsync(WindowRequest request, CancellationToken ct)
        => WindowFold.FromRawAsync(this, request, ct);

    /// <summary>
    /// Поточні значення атрибутів (синхронізація довідників, S1,
    /// FEATURE-REGISTRY-SYNC §4, ФВ-8.11): значення, якість і мітка часу на
    /// кожен шлях.
    /// </summary>
    /// <remarks>
    /// ⛔ Атрибута, якого немає в джерелі, немає і в
    /// <see cref="CurrentValuesResult.Values"/>: він іде в
    /// <see cref="CurrentValuesResult.Failures"/> зі своїм шляхом. «Нуль» чи
    /// <c>null</c> замість відмови синк прочитав би як «джерело очистило поле».
    /// Відмова всього джерела (недоступність, автентифікація) — виняток, як у
    /// <see cref="ReadAsync"/>: неповний знімок не видається за повний (D-187).
    /// <para>
    /// ⚠ Типова реалізація — <b>відмова</b> <c>ECR-INT-0422</c>
    /// (<c>.currentValueNotSupported</c>): транспорт, який не вміє поточних
    /// значень, мусить сказати це, а не повернути порожній знімок. Чинні
    /// реалізації від цього не змінюються.
    /// </para>
    /// </remarks>
    /// <param name="dataSourceId">Джерело.</param>
    /// <param name="paths">Шляхи атрибутів; повтори читаються один раз.</param>
    /// <param name="ct">Скасування.</param>
    public Task<CurrentValuesResult> ReadCurrentAsync(
        int dataSourceId, IReadOnlyCollection<string> paths, CancellationToken ct)
        => Task.FromException<CurrentValuesResult>(new BusinessRuleException(
            QueryRefusedCode,
            $"Транспорт {Transport} не читає поточних значень атрибутів.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0422.currentValueNotSupported",
                ["transport"] = Transport.ToString(),
            }));

    /// <summary>Код відмови «тип читання не налаштовано або транспорт його не виконує».</summary>
    public const string QueryRefusedCode = "ECR-INT-0422";

    /// <summary>
    /// Відмова адаптера, який тип запиту <paramref name="kind"/> не виконує взагалі
    /// (HSE301 F4, прохання до PI Web API і SQL).
    /// </summary>
    /// <remarks>
    /// ⚠ Код той самий, що в PI SQL Client без налаштованого тексту
    /// (<c>.queryKindNotConfigured</c>), а ключ — власний <c>.queryKindNotSupported</c>:
    /// шаблон <c>.queryKindNotConfigured</c> радить «задати {configKey}», а
    /// налаштування, яке ввімкнуло б цей тип для PI Web API чи SQL, не існує.
    /// Вигаданий ключ конфігурації послав би людину шукати його (і
    /// <c>MessageKeyRatchetTests</c> порожнього плейсхолдера не пропускає).
    /// </remarks>
    /// <param name="kind">Тип запиту.</param>
    /// <param name="transport">Транспорт адаптера.</param>
    public static BusinessRuleException QueryKindNotSupported(SourceQueryKind kind, ExternalTransport transport)
        => new(
            QueryRefusedCode,
            $"Запит типу {kind} транспорт {transport} не виконує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0422.queryKindNotSupported",
                ["queryKind"] = kind.ToString(),
                ["transport"] = transport.ToString(),
            });
}

/// <summary>Поточні значення атрибутів — результат <see cref="IExternalDataSource.ReadCurrentAsync"/>.</summary>
/// <param name="Values">
/// Прочитані значення в <b>одиниці джерела</b> (ФВ-16.9): <see cref="SourceDataPoint.Timestamp"/> —
/// мітка часу значення в джерелі, <see cref="SourceDataPoint.Quality"/> — якість у його термінах.
/// </param>
/// <param name="Failures">Шляхи, які прочитати не вдалося, — кожен зі своєю причиною.</param>
public sealed record CurrentValuesResult(
    IReadOnlyList<SourceDataPoint> Values,
    IReadOnlyList<CurrentValueFailure> Failures);

/// <summary>Шлях, поточне значення якого не прочитано.</summary>
/// <param name="SourcePath">Шлях атрибута.</param>
/// <param name="ErrorCode">
/// <c>ECR-INT-0404</c> — атрибута чи елемента немає; <c>ECR-INT-0503</c> — джерело відповіло без значення.
/// </param>
/// <param name="MessageKey">Ключ каталогу повідомлень.</param>
public sealed record CurrentValueFailure(string SourcePath, string ErrorCode, string MessageKey);

/// <summary>Що саме читає <see cref="IExternalDataSource.ReadAsync"/>.</summary>
public enum SourceQueryKind : byte
{
    /// <summary>Сирі (архівні) точки — як досі.</summary>
    Raw = 0,

    /// <summary>
    /// Інтерпольовані значення з кроком <see cref="CollectionRequest.Step"/>.
    /// Адаптер, у якого запит цього типу не налаштований, відмовляє
    /// <c>ECR-INT-0422</c> (<c>.queryKindNotConfigured</c>), а не підміняє сирими.
    /// </summary>
    Interpolated = 1,
}

/// <summary>Спосіб згортки вікна (PI summary type).</summary>
public enum SourceSummaryKind : byte
{
    /// <summary>Інтеграл за часом, «одиниця × секунда» (аналог PI Total, але без пастки «за добу»).</summary>
    Total = 0,

    /// <summary>Середнє, зважене за часом, по покритому часу.</summary>
    Average = 1,

    /// <summary>Найменше з придатних точок у вікні.</summary>
    Minimum = 2,

    /// <summary>Найбільше з придатних точок у вікні.</summary>
    Maximum = 3,

    /// <summary>Кількість придатних точок у вікні.</summary>
    Count = 4,
}

/// <summary>Хто порахував значення вікна.</summary>
public enum WindowComputedBy : byte
{
    /// <summary>Локальна згортка сирих точок — еталон (<c>D-172</c>).</summary>
    Local = 0,

    /// <summary>Summary джерела за налаштованим запитом.</summary>
    Server = 1,
}

/// <summary>Запит згортки одного вікна.</summary>
/// <param name="DataSourceId">Джерело.</param>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="SourcePath">Шлях атрибута.</param>
/// <param name="FromUtc">Початок вікна, включно.</param>
/// <param name="ToUtc">Кінець вікна, виключно.</param>
/// <param name="Summary">Спосіб згортки.</param>
/// <param name="IsStep">Ряд ступінчастий (значення тримається до наступної точки).</param>
/// <param name="MaxGap">
/// Розрив між точками, довший за який відрізок — прогалина; <c>null</c> — порога
/// немає. Задає й запас пошуку точок за межами вікна (<see cref="WindowFold"/>).
/// </param>
public sealed record WindowRequest(
    int DataSourceId,
    int SourceEntityId,
    string SourcePath,
    DateTime FromUtc,
    DateTime ToUtc,
    SourceSummaryKind Summary,
    bool IsStep,
    TimeSpan? MaxGap = null);

/// <summary>Згорнуте вікно.</summary>
/// <param name="Value">Число в одиниці джерела; <c>null</c> — згортати не було чого.</param>
/// <param name="SourceUnitSymbol">UOM джерела останньої використаної точки.</param>
/// <param name="PointCount">Придатних точок усередині вікна.</param>
/// <param name="PercentGood">Покрита даними частка вікна (0–100); <c>null</c> — для згорток точок.</param>
/// <param name="ComputedBy">Локально чи сервером.</param>
/// <param name="Gaps">Непокриті відрізки вікна: прогалини, погана якість, невдале читання.</param>
/// <param name="ErrorCode">Код відмови джерела; <c>null</c> — відмов не було.</param>
public sealed record WindowResult(
    decimal? Value,
    string? SourceUnitSymbol,
    int PointCount,
    decimal? PercentGood,
    WindowComputedBy ComputedBy,
    IReadOnlyList<TimeInterval> Gaps,
    string? ErrorCode);

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
/// <param name="ExternalId">
/// Незмінний ідентифікатор <b>елемента</b> AF (GUID <c>Id</c>) — те, що лягає в
/// <c>dic.RegistryExternalKey.ExternalId</c> (FEATURE-REGISTRY-SYNC §2.1, S1).
/// Для атрибута — GUID елемента, якому він належить. <c>null</c> — джерело його не дає
/// (SQL-джерело, перевизначений запит каталогу без колонки <c>ElementId</c>).
/// </param>
public sealed record SourceEntityDescriptor(
    string Code,
    string? DisplayName,
    string? EntityPath,
    string? SourceUnitSymbol,
    string? DataType,
    string? ExternalId = null);

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
/// <param name="Kind">Тип запиту (HSE301 §4.3); типове — сирі точки, як досі.</param>
/// <param name="Step">Крок; обов'язковий для <see cref="SourceQueryKind.Interpolated"/>.</param>
public sealed record CollectionRequest(
    int DataSourceId,
    int SourceEntityId,
    string SourcePath,
    DateTime FromUtc,
    DateTime ToUtc,
    int MaxPoints,
    SourceQueryKind Kind = SourceQueryKind.Raw,
    TimeSpan? Step = null);

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
