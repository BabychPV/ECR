// src/Ecr.Application/Ports/IImportPreviewStore.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Тимчасове сховище побудованих diff-ів імпорту.
/// </summary>
/// <remarks>
/// ⚠ Імпорт розділений на перегляд і застосування (ФВ-4.3), а між ними
/// проходить час і, як правило, інший HTTP-запит — можливо, до іншого
/// інстансу застосунку. Тому diff живе <b>поза пам'яттю процесу</b>: у
/// пам'яті він знайшовся б лише тоді, коли пощастило потрапити на той самий
/// інстанс, і «застосувати» ламалося б випадково.
/// <para>
/// ⛔ Строк життя обмежений навмисно. Diff — це знімок чужих даних на момент
/// перегляду; застосований через добу, він перезаписав би роботу, зроблену
/// після нього. Строк уводить межу, після якої треба переглянути ще раз.
/// </para>
/// </remarks>
public interface IImportPreviewStore
{
    /// <summary>Зберігає diff під токеном.</summary>
    /// <param name="token">Токен перегляду.</param>
    /// <param name="payloadJson">Серіалізований diff.</param>
    /// <param name="lifetime">Скільки живе.</param>
    /// <param name="ct">Скасування.</param>
    public Task SaveAsync(string token, string payloadJson, TimeSpan lifetime, CancellationToken ct);

    /// <summary>Читає diff; <c>null</c> — токена немає або строк вийшов.</summary>
    /// <param name="token">Токен перегляду.</param>
    /// <param name="ct">Скасування.</param>
    public Task<string?> FindAsync(string token, CancellationToken ct);

    /// <summary>Прибирає застосований diff.</summary>
    /// <param name="token">Токен перегляду.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Прибирається саме після застосування: інакше той самий diff можна
    /// було б застосувати двічі, а вдруге він уже писав би поверх власного
    /// результату, вважаючи його чужою правкою.
    /// </remarks>
    public Task RemoveAsync(string token, CancellationToken ct);
}
