// src/Ecr.Application/Ports/ITemplateVersionStore.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Операції над версією шаблону, які неможливо виразити через
/// <see cref="IRepository{T,TId}"/>, бо вони мусять бути атомарними в базі.
/// </summary>
/// <remarks>
/// ⚠ Порт уведений за тією самою причиною, що й порти <c>Q-018</c>: обробник
/// не має права знати про SQL, але <c>R-B7</c> вимагає саме атомарної
/// операції.
///
/// <b>Чому не read-modify-write у застосунку.</b> Інстансів застосунку
/// щонайменше два (<c>D-32</c>). Якби ревізію читали, додавали одиницю і
/// записували, два одночасні патчі дали б однакове нове значення, і другий
/// мовчки затер би перший — при цьому ключ кешу <c>v{id}:r{rev}</c> у клієнтів
/// збігся б із застарілою структурою. Тому інкремент робиться одним
/// <c>UPDATE … SET PresentationRevision = PresentationRevision + 1 OUTPUT
/// inserted.PresentationRevision</c>, і застосунок дізнається результат, а не
/// призначає його.
/// </remarks>
public interface ITemplateVersionStore
{
    /// <summary>
    /// Інкрементує <c>PresentationRevision</c> одним statement і повертає
    /// <b>нове</b> значення з <c>OUTPUT</c>.
    /// </summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Нова ревізія.</returns>
    public Task<int> IncrementPresentationRevisionAsync(int templateVersionId, CancellationToken ct);

    /// <summary>
    /// Чи існують документи, прив'язані до цієї версії.
    /// </summary>
    /// <remarks>
    /// Від відповіді залежить класифікація структурної зміни (ФВ-7.4):
    /// та сама зміна коду колонки без документів <c>Safe</c>, з документами —
    /// <c>Breaking</c> і відмова операції.
    /// </remarks>
    public Task<bool> HasDocumentsAsync(int templateVersionId, CancellationToken ct);

    /// <summary>Створює порожню чернетку версії.</summary>
    public Task<int> CreateDraftAsync(
        int templateId, string versionNumber, int userId, DateTime utcNow, CancellationToken ct);

    /// <summary>
    /// Глибокий клон структури версії зі **збереженням** <c>Code</c> і
    /// <c>RowKey</c> (ФВ-2.8).
    /// </summary>
    /// <remarks>
    /// Клонування живе у сховищі, а не в обробнику: воно неминуче знає про
    /// порядок вставки і призначення ключів базою. Обробник знає лише, що
    /// ідентичності зберігаються — інакше формули клону посилалися б у
    /// порожнечу.
    /// </remarks>
    public Task<int> CloneAsync(
        int sourceVersionId, string newVersion, int userId, DateTime utcNow, CancellationToken ct);

    /// <summary>Версії шаблону зі станом і ревізією.</summary>
    public Task<IReadOnlyList<TemplateVersionSummary>> ListVersionsAsync(
        int templateId, Common.CursorRequest page, CancellationToken ct);

    /// <summary>Створює шаблон і повертає його ідентифікатор.</summary>
    public Task<int> CreateTemplateAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        int userId,
        DateTime utcNow,
        CancellationToken ct);

    /// <summary>Сторінка шаблонів.</summary>
    public Task<Common.PagedResult<TemplateSummary>> ListTemplatesAsync(
        Common.CursorRequest page, CancellationToken ct);

    /// <summary>
    /// Застосовує презентаційні зміни до структури версії.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього методу <c>PATCH …/presentation</c> був **переконливою
    /// заглушкою**: він розбирав патч, класифікував кожну зміну, відхиляв
    /// структурні, піднімав <c>PresentationRevision</c> і писав аудит — і не
    /// змінював жодного поля. Тобто підпис колонки лишався старим, ключ кешу
    /// ставав новим, і всі клієнти перечитували структуру, щоб побачити те
    /// саме. П'ять тестів були зелені: вони перевіряли ревізію і аудит.
    ///
    /// ⚠ Дозволені поля — <b>білий список</b> у реалізації, а не назва поля
    /// з запиту в тексті SQL. Клас зміни перевіряє обробник, але сховище не
    /// має покладатися на чужу перевірку: назва поля приходить із мережі.
    ///
    /// ⚠ Кожен UPDATE обмежений ВЕРСІЄЮ. Ідентифікатор колонки теж приходить
    /// із мережі, і без цієї умови патч однієї версії міняв би підписи в
    /// будь-якій іншій.
    /// </remarks>
    /// <param name="templateVersionId">Версія, якій належать сутності.</param>
    /// <param name="changes">Зміни; усі мають бути презентаційними.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки рядків справді змінилося.</returns>
    public Task<int> ApplyPresentationAsync(
        int templateVersionId, IReadOnlyList<PresentationChange> changes, CancellationToken ct);
}

/// <summary>Одна презентаційна зміна.</summary>
/// <param name="EntityType">Тип сутності: <c>ColumnDef</c>, <c>RowDef</c>, <c>SheetDef</c>, <c>TableDef</c>.</param>
/// <param name="EntityId">Ідентифікатор сутності.</param>
/// <param name="Field">Поле; має належати презентаційному шару.</param>
/// <param name="Value">Нове значення в текстовому вигляді; <c>null</c> — стерти.</param>
public sealed record PresentationChange(string EntityType, int EntityId, string Field, string? Value);

/// <summary>Версія шаблону в переліку.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="Status">Стан: чернетка, опублікована, застаріла.</param>
/// <param name="PresentationRevision">Ревізія презентаційного шару; частина ключа кешу.</param>
/// <param name="ClonedFromVersionId">Версія-джерело, якщо це клон.</param>
/// <param name="PublishedAt">Момент публікації.</param>
public sealed record TemplateVersionSummary(
    int Id,
    string Version,
    Domain.Enums.TemplateVersionStatus Status,
    int PresentationRevision,
    int? ClonedFromVersionId,
    DateTime? PublishedAt);

/// <summary>Шаблон у переліку.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код.</param>
/// <param name="VersionCount">Скільки версій має шаблон.</param>
public sealed record TemplateSummary(int Id, string Code, int VersionCount);
