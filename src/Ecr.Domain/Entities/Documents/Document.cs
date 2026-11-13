using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Документ — екземпляр звітності; аналог «HSE-файлу» чинного рішення.</summary>
public sealed class Document : Entity<long>
{
    private readonly List<DocumentSheet> _sheets = [];

    private Document() { }

    /// <param name="projectId">Проєкт.</param>
    /// <param name="businessKey">Бізнес-ключ.</param>
    /// <param name="createdByUserId">Автор.</param>
    /// <param name="utcNow">Момент створення.</param>
    /// <param name="templateId">
    /// B74 P1: шаблон документа; <c>null</c> - основний шаблон проєкту (шаблон <c>Project.TemplateVersionId</c>). Обов'язковим
    /// стає в P3, NOT NULL - у P6 (ADR B74, Q2); до того порожні значення доповнює блок ремонту <c>99-epilogue.sql</c>.
    /// </param>
    public Document(int projectId, string businessKey, int createdByUserId, DateTime utcNow, int? templateId = null)
    {
        ProjectId = projectId;
        TemplateId = templateId;
        BusinessKey = businessKey;
        CreatedAt = utcNow;
        CreatedByUserId = createdByUserId;
        ModifiedAt = utcNow;
        ModifiedByUserId = createdByUserId;
    }

    public int ProjectId { get; private set; }

    /// <summary>B74: шаблон документа (<c>cfg.Template.Id</c>); <c>null</c> - основний шаблон проєкту.</summary>
    public int? TemplateId { get; private set; }

    /// <summary>Складається зі значень колонок із <c>IsBusinessKey</c>.</summary>
    public string BusinessKey { get; private set; } = null!;

    public LocalizedText? NameL10n { get; private set; }

    // ⛔ Статусу документа тут НЕМАЄ (D-93). Гранулярність затвердження —
    // аркуш × період (D-38), і єдине джерело істини — wf.ApprovalState.
    // Скалярний Status тут був би другим джерелом, яке рано чи пізно
    // розійдеться з першим і покаже «Approved» на документі, половина
    // аркушів якого ще в Draft. Потрібен зведений стан для списку
    // документів — рахуйте його запитом до wf.ApprovalState, не колонкою.

    public DateTime CreatedAt { get; private set; }
    public int CreatedByUserId { get; private set; }
    public DateTime ModifiedAt { get; private set; }
    public int ModifiedByUserId { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public IReadOnlyList<DocumentSheet> Sheets => _sheets;

    /// <summary>Додає аркуш у склад документа (ФВ-3.2).</summary>
    /// <param name="sheetDefId">Аркуш зі структури шаблону.</param>
    /// <remarks>
    /// Повторне додавання ігнорується: склад — це множина, а не список, і
    /// подвоєний аркуш означав би подвоєні таблиці на тому самому місці.
    /// </remarks>
    public void IncludeSheet(int sheetDefId)
    {
        if (_sheets.Exists(s => s.SheetDefId == sheetDefId))
        {
            return;
        }

        _sheets.Add(new DocumentSheet(Id, sheetDefId));
    }

    /// <summary>Фіксує зміну документа.</summary>
    public void Touch(int userId, DateTime utcNow)
    {
        ModifiedAt = utcNow;
        ModifiedByUserId = userId;
    }

    /// <summary>Змінює бізнес-ключ (ФВ-3.9); правила й аудит — у <c>ChangeDocumentKeyHandler</c>.</summary>
    public void ChangeBusinessKey(string businessKey, int userId, DateTime utcNow)
    {
        BusinessKey = businessKey;
        Touch(userId, utcNow);
    }

    /// <summary>
    /// Передає власність документа (D-443): власник — <see cref="CreatedByUserId"/>, тож передача його й
    /// змінює. Правила (хто може, кому) і журнал безпеки — у <c>TransferDocumentOwnerHandler</c>; попередній
    /// власник лишається в журналі (подія <c>DocumentOwnerTransferred</c> з обома ідентифікаторами).
    /// </summary>
    /// <param name="newOwnerUserId">Новий власник.</param>
    /// <param name="byUserId">Хто передає.</param>
    /// <param name="utcNow">Момент з <c>IClock.UtcNow</c>.</param>
    /// <returns>Попередній власник.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Ідентифікатор не додатний.</exception>
    /// <exception cref="InvalidOperationException">Новий власник — той самий.</exception>
    public int TransferOwnership(int newOwnerUserId, int byUserId, DateTime utcNow)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newOwnerUserId);

        if (newOwnerUserId == CreatedByUserId)
        {
            throw new InvalidOperationException("Користувач уже є власником документа.");
        }

        var previous = CreatedByUserId;
        CreatedByUserId = newOwnerUserId;
        Touch(byUserId, utcNow);
        return previous;
    }

    /// <summary>
    /// Задає людське ім'я документа — підпис ПОРУЧ із <see cref="BusinessKey"/>,
    /// а не замість нього (директива "людське ім'я документа").
    /// </summary>
    /// <remarks>
    /// ⛔ <c>BusinessKey</c> лишається технічним ключем: він бере участь у
    /// іменах експортів і в аудиті, і зміна його механізму — окреме рішення,
    /// не це. <c>NameL10n</c> — чисто презентаційне поле, яке нічому не
    /// заважає, якщо його не задано (<c>null</c> — поведінка як до цього поля).
    /// </remarks>
    /// <param name="name"><c>null</c> — ім'я не задано або знято.</param>
    public void SetName(LocalizedText? name) => NameL10n = name;
}
