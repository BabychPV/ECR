// src/Ecr.Domain/Entities/Documents/EditAssignment.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Передача зрізу документа виконавцям для заповнення (EA-1, <c>doc.EditAssignment</c>): власник
/// передає місяць третій стороні, виконавець заповнює, власник валідує або повертає.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Це НЕ «обмеження прав» і НЕ грант ролі: передача лише піднімає виконавцю <c>Read → Write</c>
/// у комірках зрізу, які він уже бачить, і (за <see cref="IsExclusive"/>) закриває зріз іншим.
/// Читання вона не дає ніколи. Правила застосування — <c>AccessDecisionService</c> / <c>EditRules</c>
/// (EA-2); тут лише дані й інваріанти.
/// </para>
/// <para>
/// ⚠ Стан виконавця — <see cref="EditAssignmentRecipient"/> (по кожному окремо), стан валідації —
/// тут, на зрізі. Переходи (Fill/Validate/Return) додає EA-1b; EA-1a — лише форма даних.
/// </para>
/// <para>
/// ⚠ Видалення документа-чернетки прибирає передачі явно (<c>DocumentDeletionStore</c>): каскаду
/// на FK немає за конвенцією бази.
/// </para>
/// </remarks>
public sealed class EditAssignment : Entity<long>
{
    /// <summary>Стеля передач на документ.</summary>
    public const int MaxPerDocument = 200;

    /// <summary>Стеля строку дії передачі, діб від моменту створення.</summary>
    public const int MaxLifetimeDays = 366;

    /// <summary>Стеля виконавців у одній передачі.</summary>
    public const int MaxRecipients = 100;

    /// <summary>Межа довжини <see cref="Note"/>, <see cref="AppliedSetName"/> — див. <see cref="EditorSet.MaxNameLength"/>.</summary>
    public const int MaxNoteLength = 500;

    private readonly List<EditAssignmentRecipient> _recipients = [];

    private EditAssignment() { }

    /// <summary>Створює передачу без виконавців; виконавців додає <see cref="AddRecipient"/>.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="slice">Зріз (канонічний ключ рахується тут).</param>
    /// <param name="isExclusive">Чи закривати зріз іншим за ролями (за замовчуванням ТАК, D1).</param>
    /// <param name="delegatedByUserId">Чиїми правами обмежено передачу (власник/менеджер у момент передачі).</param>
    /// <param name="handoffId">Група однієї дії «Передати» (apply-set/copy → кілька зрізів).</param>
    /// <param name="createdByUserId">Хто створив (звичайно збігається з <paramref name="delegatedByUserId"/>).</param>
    /// <param name="utcNow">Момент з <c>IClock.UtcNow</c>.</param>
    /// <param name="expiresAt">Необов'язковий термін дії передачі (UTC).</param>
    /// <param name="note">Інструкція виконавцям (звичайний текст).</param>
    public EditAssignment(
        long documentId,
        EditAssignmentSlice slice,
        bool isExclusive,
        int delegatedByUserId,
        Guid handoffId,
        int createdByUserId,
        DateTime utcNow,
        DateTime? expiresAt = null,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(documentId);

        if (handoffId == Guid.Empty)
        {
            throw new ArgumentException("Ідентифікатор групи передачі не може бути порожнім.", nameof(handoffId));
        }

        // Час — завжди UTC: Local переводимо, Unspecified вважаємо UTC.
        expiresAt = expiresAt is { } raw
            ? (raw.Kind == DateTimeKind.Local ? raw.ToUniversalTime() : DateTime.SpecifyKind(raw, DateTimeKind.Utc))
            : null;

        // Термін дії: не в минулому й не далі за стелю (аудит P3).
        if (expiresAt is { } expiry && (expiry <= utcNow || expiry > utcNow.AddDays(MaxLifetimeDays)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAt), expiresAt, $"Термін дії передачі має бути в майбутньому й не пізніше ніж через {MaxLifetimeDays} діб.");
        }

        DocumentId = documentId;
        SheetCode = slice.SheetCode;
        TableCode = slice.TableCode;
        MonthNumber = slice.MonthNumber;
        PeriodKeyValue = slice.PeriodKey;
        SliceKey = slice.Key;
        IsExclusive = isExclusive;
        ValidationStatus = EditAssignmentValidationStatus.NotValidated;
        DelegatedByUserId = delegatedByUserId;
        HandoffId = handoffId;
        ExpiresAt = expiresAt;
        Note = NormalizeNote(note);
        CreatedAt = utcNow;
        CreatedByUserId = createdByUserId;
        ModifiedAt = utcNow;
        ModifiedByUserId = createdByUserId;
    }

    /// <summary>Документ, зріз якого передано.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Код аркуша; <c>null</c> — усі аркуші.</summary>
    public string? SheetCode { get; private set; }

    /// <summary>Код таблиці; <c>null</c> — усі таблиці аркуша (потребує <see cref="SheetCode"/>).</summary>
    public string? TableCode { get; private set; }

    /// <summary>Номер місяця 1..12 для місячних колонок; <c>null</c> — будь-який.</summary>
    public byte? MonthNumber { get; private set; }

    /// <summary>Ключ періоду <c>Рік*100+№</c> (колонка <c>PeriodKey</c>); <c>null</c> — будь-який.</summary>
    public int? PeriodKeyValue { get; private set; }

    /// <summary>Канонічний ключ зрізу; унікальний у межах документа.</summary>
    public string SliceKey { get; private set; } = null!;

    /// <summary>Чи недоступний зріз тим, хто не виконавець (D1; зняття — «Дозволити й іншим за ролями»).</summary>
    public bool IsExclusive { get; private set; }

    /// <summary>Стан валідації зрізу власником.</summary>
    public EditAssignmentValidationStatus ValidationStatus { get; private set; }

    /// <summary>Коли валідовано (UTC); <c>null</c>, поки не валідовано.</summary>
    public DateTime? ValidatedAt { get; private set; }

    /// <summary>Хто валідував; <c>null</c>, поки не валідовано.</summary>
    public int? ValidatedByUserId { get; private set; }

    /// <summary>
    /// Чиїми правами обмежено передачу. Передача діє, лише поки цей користувач активний (R7); межі
    /// «лише на комірки, які сам може редагувати» знімаються З НЬОГО В МОМЕНТ ПЕРЕДАЧІ.
    /// </summary>
    public int DelegatedByUserId { get; private set; }

    /// <summary>Група однієї дії «Передати».</summary>
    public Guid HandoffId { get; private set; }

    /// <summary>Термін дії передачі (UTC); <c>null</c> — безстрокова, до валідації чи зняття.</summary>
    public DateTime? ExpiresAt { get; private set; }

    /// <summary>Інструкція виконавцям; звичайний текст.</summary>
    public string? Note { get; private set; }

    /// <summary>Набір, з якого застосовано склад; <c>null</c> — склад вибрано вручну чи набір видалено.</summary>
    public int? AppliedSetId { get; private set; }

    /// <summary>Знімок назви набору на момент застосування.</summary>
    public string? AppliedSetName { get; private set; }

    /// <summary>Коли створено (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Хто створив.</summary>
    public int CreatedByUserId { get; private set; }

    /// <summary>Коли востаннє змінено (UTC).</summary>
    public DateTime ModifiedAt { get; private set; }

    /// <summary>Хто востаннє змінив.</summary>
    public int ModifiedByUserId { get; private set; }

    /// <summary>Мітка оптимістичної конкурентності.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Виконавці передачі.</summary>
    public IReadOnlyList<EditAssignmentRecipient> Recipients => _recipients;

    /// <summary>Зріз передачі як значення.</summary>
    public EditAssignmentSlice Slice => EditAssignmentSlice.Create(SheetCode, TableCode, MonthNumber, PeriodKeyValue);

    /// <summary>
    /// Додає виконавця зі станом <see cref="EditAssignmentRecipientStatus.Assigned"/>; повторне додавання
    /// того самого користувача ігнорується (склад — множина).
    /// </summary>
    /// <returns><c>true</c>, якщо виконавця додано; <c>false</c>, якщо він уже був.</returns>
    /// <exception cref="InvalidOperationException">Перевищено <see cref="MaxRecipients"/>.</exception>
    public bool AddRecipient(int userId, int assignedByUserId, DateTime utcNow)
    {
        if (_recipients.Exists(r => r.UserId == userId))
        {
            return false;
        }

        if (_recipients.Count >= MaxRecipients)
        {
            throw new InvalidOperationException($"У передачі не може бути більше {MaxRecipients} виконавців.");
        }

        _recipients.Add(new EditAssignmentRecipient(Id, userId, assignedByUserId, utcNow));

        // Новий виконавець ще не заповнив: раніше дана валідація більше не справджується.
        ResetValidation(assignedByUserId, utcNow);
        return true;
    }

    /// <summary>Чи всі виконавці зрізу позначили «Заповнено» (і виконавці є).</summary>
    public bool AllFilled =>
        _recipients.Count > 0 && _recipients.TrueForAll(r => r.Status == EditAssignmentRecipientStatus.Filled);

    /// <summary>Знаходить виконавця за користувачем; <c>null</c> — такого немає.</summary>
    public EditAssignmentRecipient? FindRecipient(int userId) => _recipients.Find(r => r.UserId == userId);

    /// <summary>Прибирає виконавця зі складу. Повертає <c>false</c>, якщо виконавця не було.</summary>
    public bool RemoveRecipient(int userId, int byUserId, DateTime utcNow)
    {
        var recipient = FindRecipient(userId);
        if (recipient is null)
        {
            return false;
        }

        _recipients.Remove(recipient);

        // Склад змінився — раніше дана валідація більше не справджується (аудит P3).
        ResetValidation(byUserId, utcNow);
        return true;
    }

    /// <summary>
    /// Виконавець позначає «Заповнено» (<c>Assigned/Returned → Filled</c>). Повтор на <c>Filled</c> — без змін.
    /// </summary>
    /// <returns><c>true</c>, якщо стан змінено; <c>false</c> — виконавець уже в <c>Filled</c> (ідемпотентність).</returns>
    /// <exception cref="InvalidOperationException">Виконавця немає в передачі.</exception>
    public bool FillRecipient(int userId, DateTime utcNow)
    {
        var recipient = FindRecipient(userId)
            ?? throw new InvalidOperationException("Користувача немає серед виконавців передачі.");

        if (recipient.Status == EditAssignmentRecipientStatus.Filled)
        {
            return false;
        }

        recipient.MarkFilled(utcNow);
        Touch(userId, utcNow);
        return true;
    }

    /// <summary>
    /// Власник валідує зріз: усі виконавці мусять бути <c>Filled</c>. Повтор на <c>Validated</c> — без змін.
    /// </summary>
    /// <returns><c>true</c>, якщо стан змінено.</returns>
    /// <exception cref="InvalidOperationException">Не всі виконавці заповнили.</exception>
    public bool Validate(int byUserId, DateTime utcNow)
    {
        if (ValidationStatus == EditAssignmentValidationStatus.Validated)
        {
            return false;
        }

        if (!AllFilled)
        {
            throw new InvalidOperationException("Валідувати можна лише коли всі виконавці заповнили зріз.");
        }

        ValidationStatus = EditAssignmentValidationStatus.Validated;
        ValidatedAt = utcNow;
        ValidatedByUserId = byUserId;
        Touch(byUserId, utcNow);
        return true;
    }

    /// <summary>
    /// Власник повертає виконавця на доопрацювання (<c>Filled → Returned</c>) з причиною; валідація скидається.
    /// </summary>
    /// <exception cref="InvalidOperationException">Виконавця немає або він не в стані <c>Filled</c>.</exception>
    /// <exception cref="ArgumentException">Причина порожня чи довша за <see cref="MaxNoteLength"/>.</exception>
    public void ReturnRecipient(int userId, int byUserId, string reason, DateTime utcNow)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxNoteLength)
        {
            throw new ArgumentException($"Причина повернення — 1..{MaxNoteLength} символів.", nameof(reason));
        }

        var recipient = FindRecipient(userId)
            ?? throw new InvalidOperationException("Користувача немає серед виконавців передачі.");

        if (recipient.Status != EditAssignmentRecipientStatus.Filled)
        {
            throw new InvalidOperationException("Повернути можна лише виконавця зі станом «Заповнено».");
        }

        recipient.MarkReturned(byUserId, text, utcNow);
        ResetValidation(byUserId, utcNow);
    }

    /// <summary>Змінює ексклюзивність і примітку (склад і зріз — окремо).</summary>
    public void Reconfigure(bool isExclusive, string? note, int byUserId, DateTime utcNow)
    {
        IsExclusive = isExclusive;
        Note = NormalizeNote(note);
        Touch(byUserId, utcNow);
    }

    /// <summary>Фіксує, що склад застосовано з набору (знімок назви); <c>null</c> — склад вибрано вручну.</summary>
    public void MarkAppliedSet(int? setId, string? setName)
    {
        AppliedSetId = setId;
        AppliedSetName = setId is null ? null : setName;
    }

    /// <summary>Переприв'язує делегатора (межі прав знімаються з нього в момент зміни складу/зрізу).</summary>
    public void Redelegate(int delegatedByUserId, DateTime utcNow)
    {
        DelegatedByUserId = delegatedByUserId;
        Touch(delegatedByUserId, utcNow);
    }

    private void ResetValidation(int byUserId, DateTime utcNow)
    {
        ValidationStatus = EditAssignmentValidationStatus.NotValidated;
        ValidatedAt = null;
        ValidatedByUserId = null;
        Touch(byUserId, utcNow);
    }

    private void Touch(int byUserId, DateTime utcNow)
    {
        ModifiedAt = utcNow;
        ModifiedByUserId = byUserId;
    }

    private static string? NormalizeNote(string? note)
    {
        var trimmed = note?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > MaxNoteLength
            ? throw new ArgumentException($"Примітка довша за {MaxNoteLength} символів.", nameof(note))
            : trimmed;
    }
}

/// <summary>
/// Виконавець у передачі зрізу (EA-1, <c>doc.EditAssignmentRecipient</c>): власний стан
/// <see cref="Status"/> і власні відмітки життєвого циклу.
/// </summary>
/// <remarks>
/// ⚠ «Хто заповнив» ≡ <see cref="UserId"/> цього рядка: позначити «Заповнено» може лише сам виконавець,
/// тому окремого поля <c>FilledBy</c> немає навмисно. Історія переходів — у <c>aud.SecurityEvent</c>;
/// у рядку лише поточний стан і останні відмітки.
/// </remarks>
public sealed class EditAssignmentRecipient
{
    private EditAssignmentRecipient() { }

    /// <summary>Створює виконавця у стані <see cref="EditAssignmentRecipientStatus.Assigned"/>.</summary>
    public EditAssignmentRecipient(long assignmentId, int userId, int assignedByUserId, DateTime utcNow)
    {
        AssignmentId = assignmentId;
        UserId = userId;
        Status = EditAssignmentRecipientStatus.Assigned;
        AssignedAt = utcNow;
        AssignedByUserId = assignedByUserId;
        StatusChangedAt = utcNow;
    }

    /// <summary>Передача.</summary>
    public long AssignmentId { get; private set; }

    /// <summary>Виконавець (<c>sec.User</c>).</summary>
    public int UserId { get; private set; }

    /// <summary>Поточний стан виконавця.</summary>
    public EditAssignmentRecipientStatus Status { get; private set; }

    /// <summary>Коли призначено (UTC).</summary>
    public DateTime AssignedAt { get; private set; }

    /// <summary>Хто призначив.</summary>
    public int AssignedByUserId { get; private set; }

    /// <summary>Коли виконавець позначив «Заповнено» (UTC); зберігається й після повернення як остання відмітка.</summary>
    public DateTime? FilledAt { get; private set; }

    /// <summary>Коли власник повернув (UTC).</summary>
    public DateTime? ReturnedAt { get; private set; }

    /// <summary>Хто повернув.</summary>
    public int? ReturnedByUserId { get; private set; }

    /// <summary>Причина повернення (1–500 символів); обов'язкова для стану Returned.</summary>
    public string? ReturnReason { get; private set; }

    /// <summary>Скільки разів виконавця повертали.</summary>
    public short ReturnCount { get; private set; }

    /// <summary>Коли стан змінювався востаннє (UTC).</summary>
    public DateTime StatusChangedAt { get; private set; }

    /// <summary>Мітка оптимістичної конкурентності.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Перехід у <c>Filled</c>; викликає лише <see cref="EditAssignment"/>.</summary>
    internal void MarkFilled(DateTime utcNow)
    {
        Status = EditAssignmentRecipientStatus.Filled;
        FilledAt = utcNow;
        StatusChangedAt = utcNow;
    }

    /// <summary>Перехід у <c>Returned</c>; викликає лише <see cref="EditAssignment"/>.</summary>
    internal void MarkReturned(int byUserId, string reason, DateTime utcNow)
    {
        Status = EditAssignmentRecipientStatus.Returned;
        ReturnedAt = utcNow;
        ReturnedByUserId = byUserId;
        ReturnReason = reason;
        ReturnCount++;
        StatusChangedAt = utcNow;
    }
}
