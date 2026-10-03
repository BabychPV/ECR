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

    /// <summary>Хто виконує запит (ставить <c>SecurityStampMiddleware</c> після перевірки штампа).</summary>
    /// <param name="userId">Користувач із cookie.</param>
    public void BindRequestUser(int userId) => _requestUserId = userId;

    /// <summary>
    /// Сховище щойно прокрутило штамп <paramref name="user"/>: якщо це
    /// виконавець запиту — запам'ятати новий штамп.
    /// </summary>
    /// <param name="user">Користувач із уже новим штампом.</param>
    public void Observe(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (_requestUserId is { } id && user.Id == id)
        {
            NewStamp = user.SecurityStamp;
        }
    }
}
