// src/Ecr.Application/Registries/RegistryWriteRetry.cs
using Ecr.Application.Errors;
using Ecr.Domain.Errors;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Application.Registries;

/// <summary>
/// B167: один повтор ідемпотентного запису даних довідника (правка запису, імпорт CSV), що програв одночасному однаковому
/// запиту, — у СВІЖІЙ області DI, щоб відповідь була тією самою, що й у послідовного повтору.
/// </summary>
/// <remarks>
/// ⛔ Обидва записи — без токена версії («стан має бути таким») і читають наявні значення ДО транзакції. Другий із двох
/// одночасних однакових запитів програвав унікальному індексу (<c>UQ_RegistryValue</c>) чи ревізії довідника — загальний
/// <c>409 ECR-CELL-0409</c>, хоча послідовний повтор — <c>200</c>. Повтор читає закомічене переможцем і проходить тим самим
/// шляхом, що й послідовний повтор (upsert — той самий <c>200</c>; імпорт — <c>applied=false</c>, <c>unchanged</c>).
/// <para>
/// ⚠ Свіжа область, а не той самий обробник: трекер змін першої спроби брудний (додані значення, піднята ревізія). Повтор —
/// рівно один і лише на загальному конфлікті; інші відмови (зміна опису, ключі, правила, зайнятий код) — без повтору.
/// </para>
/// </remarks>
public sealed class RegistryWriteRetry(IServiceScopeFactory scopes)
{
    /// <summary>Виконує <paramref name="call"/> над <paramref name="handler"/>; програш гонки — ще раз над свіжим обробником.</summary>
    /// <typeparam name="THandler">Обробник (зареєстрований у контейнері).</typeparam>
    /// <typeparam name="T">Результат.</typeparam>
    /// <param name="handler">Обробник поточного запиту.</param>
    /// <param name="call">Виклик обробника.</param>
    /// <returns>Результат першої або повторної спроби.</returns>
    public async Task<T> RunAsync<THandler, T>(THandler handler, Func<THandler, Task<T>> call)
        where THandler : notnull
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            return await call(handler).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException ex) when (ex.ErrorCode == ErrorCodes.CellConflict)
        {
            await using var scope = scopes.CreateAsyncScope();
            return await call(scope.ServiceProvider.GetRequiredService<THandler>()).ConfigureAwait(false);
        }
    }
}
