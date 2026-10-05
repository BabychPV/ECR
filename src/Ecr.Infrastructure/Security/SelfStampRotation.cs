using Ecr.Domain.Entities.Security;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Маркер запиту: чи прокрутив ЦЕЙ запит штамп самого виконавця і яким він став.
/// </summary>
/// <remarks>
/// ⛔ L1-01 (аудит 2026-10-03). До маркера <c>SecurityStampMiddleware</c>
/// перевидавав cookie з новим штампом на будь-який успішний не-GET, щойно штамп
/// у БД відрізнявся від штампа в cookie, — не питаючи, ХТО його прокрутив. А
/// ротація штампа — єдиний механізм відкликання для блокування, скидання й
/// зміни пароля, «вийти з усіх». Викрадена cookie, що встигла зробити запит у
/// 5-секундному вікні кешу, отримувала свіжу cookie на 12 год.
///
/// Тепер перевидання — лише коли штамп прокрутив сам цей запит (власні ролі,
/// гранти власної ролі), і лише на те значення, яке він записав. Будь-яка чужа
/// ротація між ними дає інше значення в БД і cookie не перевидається.
///
/// Scoped: живе рівно один запит.
/// </remarks>
public sealed class SelfStampRotation
{
    private int? _requestUserId;

    /// <summary>Штамп, який записав цей запит виконавцю; <c>null</c> — не записував.</summary>
    public string? NewStamp { get; private set; }

    /// <summary>
    /// Штамп виконавця в БД ДО першої ротації цим запитом.
    /// </summary>
    /// <remarks>
    /// ⛔ AN-26b (рев'ю AN-26, P2): відкликана cookie зі старим штампом A, що
    /// проскочила перевірку з кешу, сама крутила B→C власною зміною ролей і
    /// отримувала cookie з C. Перевидання дозволене, лише коли ротацію почато
    /// від штампа самої cookie — тобто ніхто не відкликав її раніше.
    /// </remarks>
    public string? PreviousStamp { get; private set; }

    /// <summary>Хто виконує запит (ставить <c>SecurityStampMiddleware</c> після перевірки штампа).</summary>
    /// <param name="userId">Користувач із cookie.</param>
    public void BindRequestUser(int userId) => _requestUserId = userId;

    /// <summary>
    /// Сховище щойно прокрутило штамп <paramref name="user"/>: якщо це
    /// виконавець запиту — запам'ятати новий штамп.
    /// </summary>
    /// <param name="user">Користувач із уже новим штампом.</param>
    /// <param name="previousStamp">Його штамп до цієї ротації.</param>
    public void Observe(User user, string previousStamp)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (_requestUserId is { } id && user.Id == id)
        {
            // Друга ротація в тому самому запиті не підміняє точку відліку.
            PreviousStamp ??= previousStamp;
            NewStamp = user.SecurityStamp;
        }
    }
}
