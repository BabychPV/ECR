// src/Ecr.Application/Ports/ICellPatcher.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Ports;

/// <summary>
/// Запис комірок від імені інтеграції (<c>D-118</c>).
/// </summary>
/// <remarks>
/// ⛔ Порт існує, щоб інтеграція писала ТИМ САМИМ шляхом, що й людина, а не
/// власним. Директива забороняє другий шлях запису прямо, і причина названа:
/// саме другий шлях дав `A7-27` — там мапа колонок будувалася інакше, ніж на
/// основному, і адресація розійшлася.
///
/// ⚠ Порт у прикладному шарі, а реалізація поверх <c>PatchCellsHandler</c>:
/// задача живе в інфраструктурі й не має посилатися на обробники напряму.
/// </remarks>
public interface ICellPatcher
{
    /// <summary>Записує згорнуті значення збору в комірки.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="cells">Значення: рядок, колонка, число.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки записано і які комірки лишено за людиною.</returns>
    /// <remarks>
    /// ⛔ Комірку з правкою людини реалізація НЕ перезаписує (`D-118`): людина
    /// виправила навмисно, і інтеграція не має права це стерти. Але й мовчати
    /// не можна — такі комірки повертаються переліком, і викликач кладе їх у
    /// журнал покриття.
    /// </remarks>
    public Task<IntegrationWriteResult> ApplyIntegrationAsync(
        long documentId,
        long tableInstanceId,
        PeriodKey periodKey,
        IReadOnlyList<IntegrationCellValue> cells,
        CancellationToken ct);

    /// <summary>
    /// Записує рядки від інтеграції з типізованими значеннями — створює
    /// відсутні й оновлює наявні (HSE301 A5a, <c>FEATURE-HSE301-VIEW.md</c> §4.7.4 п. 5).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="rows">Рядки: ключ і типізовані значення колонок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Той самий наслідок, що й у <see cref="ApplyIntegrationAsync"/>, плюс
    /// <see cref="IntegrationWriteResult.Rejected"/> — значення, яких колонка не приймає.
    /// </returns>
    /// <remarks>
    /// ⛔ Ті самі гарантії, що й у <see cref="ApplyIntegrationAsync"/>, і той
    /// самий шлях запису: правка людини не перезаписується, незмінене не
    /// пишеться, конфлікт версії — обмежений повтор. Версію рядка викликач НЕ
    /// передає: її бере реалізація (наявний рядок — його версія, відсутній —
    /// <c>null</c>, R-B2), і рядок, який хтось створив між читанням і записом,
    /// стає оновленням, а не дублем.
    ///
    /// ⚠ Тіло за замовчуванням — відмова: метод адитивний, а підробки порту в
    /// тестах (<c>ICellPatcher</c> поверх справжнього) пишуть лише комірки.
    /// </remarks>
    public Task<IntegrationWriteResult> ApplyIntegrationRowsAsync(
        long documentId,
        long tableInstanceId,
        PeriodKey periodKey,
        IReadOnlyList<IntegrationRowUpsert> rows,
        CancellationToken ct)
        => throw new NotSupportedException($"{GetType().Name} не пише рядків від інтеграції.");
}

/// <summary>Одне згорнуте значення для запису.</summary>
/// <param name="RowKey">Рядок-адресат із мапінгу.</param>
/// <param name="ColumnDefId">Колонка-адресат.</param>
/// <param name="Value">Згорнуте значення.</param>
public sealed record IntegrationCellValue(string RowKey, int ColumnDefId, decimal Value);

/// <summary>Рядок від інтеграції: ключ і типізовані значення колонок (HSE301 A5a).</summary>
/// <param name="RowKey">Ключ рядка; для події — <see cref="EventRowKey"/>.</param>
/// <param name="Cells">Значення колонок; колонки, яких тут немає, не чіпаються.</param>
public sealed record IntegrationRowUpsert(string RowKey, IReadOnlyList<IntegrationRowCell> Cells)
{
    /// <summary>Префікс ключа рядка, заведеного з події джерела.</summary>
    public const string EventPrefix = "EF-";

    /// <summary>
    /// Ключ рядка події: <c>EF-</c> + ID події, якщо разом вони проходять
    /// <see cref="Ecr.Domain.ValueObjects.RowKey.Pattern"/>, інакше <c>EF-</c> + 32 hex SHA-256 від ID (§4.7.4 п. 2).
    /// </summary>
    /// <param name="eventId">ID події в джерелі.</param>
    /// <remarks>
    /// ⛔ Ключ детермінований: той самий ID дає той самий рядок, тож повтор
    /// синхронізації оновлює, а не дублює. Ручні рядки мають ключ-GUID без
    /// дефісів і префікса (<c>RowKey.NewDynamic</c>) — з цим не перетинаються.
    /// </remarks>
    public static string EventRowKey(string eventId)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventId);

        var direct = EventPrefix + eventId;
        if (Ecr.Domain.ValueObjects.RowKey.TryCreate(direct, out _))
        {
            return direct;
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(eventId));
        return EventPrefix + Convert.ToHexStringLower(hash)[..32];
    }
}

/// <summary>Типізоване значення однієї колонки рядка від інтеграції.</summary>
/// <param name="ColumnDefId">Колонка-адресат.</param>
/// <param name="Value">Значення.</param>
public sealed record IntegrationRowCell(int ColumnDefId, IntegrationValue Value);

/// <summary>
/// Значення від інтеграції з явним видом: число, текст, дата або запис довідника.
/// </summary>
/// <remarks>
/// ⚠ Вид — частина значення, а не здогадка за колонкою: Id запису довідника й
/// число однаково приходять цілими, і лише вид відрізняє «запис 12» від «12».
/// Колонка <c>Lookup</c> приймає лише <see cref="IntegrationValueKind.Lookup"/>, і навпаки.
/// </remarks>
public sealed record IntegrationValue
{
    private IntegrationValue(IntegrationValueKind kind, object raw)
    {
        Kind = kind;
        Raw = raw;
    }

    /// <summary>Вид значення.</summary>
    public IntegrationValueKind Kind { get; }

    /// <summary>
    /// Значення для запису: <see cref="decimal"/>, <see cref="string"/>,
    /// <see cref="DateTime"/> або <see cref="long"/> (Id запису).
    /// </summary>
    /// <remarks>
    /// ⛔ Ніколи не <c>null</c>: <c>PatchCell</c> з <c>null</c> означає «СТЕРТИ
    /// комірку» (R-B4), а не «порожнє значення».
    /// </remarks>
    public object Raw { get; }

    /// <summary>Число.</summary>
    /// <param name="value">Значення.</param>
    public static IntegrationValue Number(decimal value) => new(IntegrationValueKind.Number, value);

    /// <summary>Текст.</summary>
    /// <param name="value">Значення; не <c>null</c>.</param>
    public static IntegrationValue Text(string value)
        => new(IntegrationValueKind.Text, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Дата й час — у часовому поясі проєкту (<c>D-179</c>), як їх бачить людина.</summary>
    /// <param name="value">Значення.</param>
    public static IntegrationValue Date(DateTime value) => new(IntegrationValueKind.Date, value);

    /// <summary>Запис довідника колонки.</summary>
    /// <param name="entryId">Id запису.</param>
    public static IntegrationValue Lookup(long entryId) => new(IntegrationValueKind.Lookup, entryId);
}

/// <summary>Вид значення від інтеграції.</summary>
public enum IntegrationValueKind
{
    /// <summary>Число.</summary>
    Number,

    /// <summary>Текст.</summary>
    Text,

    /// <summary>Дата й час.</summary>
    Date,

    /// <summary>Id запису довідника.</summary>
    Lookup,
}

/// <summary>Наслідок запису від інтеграції.</summary>
/// <param name="Applied">Скільки комірок записано.</param>
/// <param name="KeptManual">
/// Комірки, лишені за людиною: <c>rowKey:columnCode</c>. ⛔ Лише справжні
/// правки людини (і видалені колонки) — не повтори й не підтвердження: кожен
/// елемент тут стає в журналі «має правку людини».
/// </param>
/// <param name="AwaitingConfirmation">
/// Комірки, на які правило періоду вимагає підтвердження людини
/// (<c>ФВ-2.16</c>, <c>AllowWithConfirmation</c>): інтеграція підтверджувати не
/// може, тож їх не записано. <c>null</c> — таких немає.
/// </param>
/// <param name="WriteConflicts">
/// Комірки, не записані через те, що рядок змінювали під час запису, і
/// обмежені повтори вичерпано: <c>rowKey:columnCode</c>. <c>null</c> — таких
/// немає. Людина їх не правила — окремо від <paramref name="KeptManual"/>.
/// </param>
/// <param name="Rejected">
/// Лише для <see cref="ICellPatcher.ApplyIntegrationRowsAsync"/>: значення, яких
/// колонка не приймає, — не той вид чи тип, або запис не з довідника колонки
/// (чужий, відсутній, видалений, вимкнений, нечинний на кінець періоду):
/// <c>rowKey:columnCode</c>. Не записані, решта рядка — записана. <c>null</c> — таких немає.
/// </param>
public sealed record IntegrationWriteResult(
    int Applied,
    IReadOnlyList<string> KeptManual,
    IReadOnlyList<string>? AwaitingConfirmation = null,
    IReadOnlyList<string>? WriteConflicts = null,
    IReadOnlyList<string>? Rejected = null);

/// <summary>
/// Журнал покриття збору (<c>itg.CollectionCoverage</c>).
/// </summary>
/// <remarks>
/// ⚠ **Ознака здоров'я — саме журнал, а не тиша.** Прогалина в покритті
/// означає, що даних за проміжок немає, — і це видно, лише якщо покриття
/// записується (ІНТ-3.3).
/// </remarks>
public interface ICoverageJournal
{
    /// <summary>Записує подію покриття.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="status">Статус із <c>CollectionCoverage.KnownStatuses</c>.</param>
    /// <param name="details">Пояснення для людини; без стеків (ФВ-6.11).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RecordAsync(
        int sourceEntityId, PeriodKey periodKey, string status, string details, CancellationToken ct);

    /// <summary>Записує кілька подій покриття ОДНИМ <c>SaveChangesAsync</c>.</summary>
    /// <remarks>
    /// ⛔ Q-170 (аудит фази 2, продуктивність). Виклик <see cref="RecordAsync"/>
    /// у циклі на кожен конфлікт «залишено за людиною» коштує окремого
    /// <c>SaveChangesAsync</c> на кожен запис; тут — один похід на весь набір.
    /// </remarks>
    public Task RecordManyAsync(IReadOnlyList<CoverageEvent> events, CancellationToken ct);
}

/// <summary>Одна подія покриття для пакетного запису.</summary>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Status">Статус із <c>CollectionCoverage.KnownStatuses</c>.</param>
/// <param name="Details">Пояснення для людини; без стеків (ФВ-6.11).</param>
public sealed record CoverageEvent(int SourceEntityId, PeriodKey PeriodKey, string Status, string Details);
