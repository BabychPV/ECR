namespace Ecr.Api.Contracts;

/// <summary>
/// Тіла відповідей, спільні для кількох контролерів.
/// </summary>
/// <remarks>
/// ⚠ Файла немає в дереві <c>05-skeleton.md</c> §1: він з'явився з аудиту
/// (<c>A7-44</c>).
///
/// ⛔ Чотирнадцять дій повертали <b>анонімний об'єкт</b>
/// (<c>return Created(…, new { projectId })</c>). У схемі OpenAPI на його
/// місці лишалося порожнє тіло, тому згенерувати клієнтський тип не було з
/// чого — і клієнт описував відповідь рукописним літералом
/// <c>apiFetch&lt;{ projectId: number }&gt;(…)</c>. Це той самий дефект, що
/// <c>A7-16</c>, <c>A7-32</c>, <c>A7-34</c>…<c>A7-38</c>: форма живе у двох
/// місцях, і ніщо їх не звіряє. Помилка в назві поля тут нічого не ламає —
/// вона просто дає <c>undefined</c> там, де компілятор обіцяв число.
///
/// ⚠ Записи named, а не <c>record struct</c>: у схемі вони мають бути
/// об'єктами з іменованими полями, і саме за цими іменами клієнт їх читає.
/// </remarks>
/// <param name="JobId">Ідентифікатор фонової задачі для опитування стану.</param>
public sealed record JobAcceptedResponse(string JobId);

/// <summary>Створений проєкт.</summary>
/// <param name="ProjectId">Ідентифікатор.</param>
public sealed record ProjectIdResponse(int ProjectId);

/// <summary>Створений шаблон.</summary>
/// <param name="TemplateId">Ідентифікатор.</param>
public sealed record TemplateIdResponse(int TemplateId);

/// <summary>Створена версія шаблону.</summary>
/// <param name="VersionId">Ідентифікатор.</param>
public sealed record VersionIdResponse(int VersionId);

/// <summary>Створений документ.</summary>
/// <param name="DocumentId">Ідентифікатор.</param>
public sealed record DocumentIdResponse(long DocumentId);

/// <summary>Створена роль.</summary>
/// <param name="RoleId">Ідентифікатор.</param>
public sealed record RoleIdResponse(int RoleId);

/// <summary>
/// Створений користувач.
/// </summary>
/// <remarks>⛔ Ані пароля, ані його хеша тут немає і бути не може (ФВ-6.11).</remarks>
/// <param name="UserId">Ідентифікатор.</param>
public sealed record UserIdResponse(int UserId);

/// <summary>Створений рядок динамічної таблиці.</summary>
/// <param name="RowKey">Ключ рядка; на нього посилаються формули й аудит.</param>
public sealed record RowKeyResponse(string RowKey);

/// <summary>
/// Розпочатий сеанс симуляції.
/// </summary>
/// <remarks>
/// ⚠ Несе і суб'єкта, і прапорець «лише читання»: клієнт зобов'язаний
/// показувати банер увесь сеанс (ФВ-6.16a), а для цього йому мало
/// ідентифікатора.
/// </remarks>
/// <param name="SessionId">Сеанс; ним же він і завершується.</param>
/// <param name="SimulatedForUserId">Чиїми правами дивимося.</param>
/// <param name="ReadOnly">Завжди <c>true</c>: будь-який запис під симуляцією відхиляється.</param>
public sealed record SimulationSessionResponse(long SessionId, int SimulatedForUserId, bool ReadOnly);

/// <summary>
/// Прийнятий у чергу перерахунок.
/// </summary>
/// <param name="JobId">Задача.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період; перерахунок завжди адресує пару документ × період.</param>
public sealed record RecalculationAcceptedResponse(string JobId, long DocumentId, int PeriodKey);
