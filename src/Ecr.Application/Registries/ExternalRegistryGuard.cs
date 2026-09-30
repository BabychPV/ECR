// src/Ecr.Application/Registries/ExternalRegistryGuard.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Registries;

/// <summary>
/// Записи довідника з master-джерелом <see cref="RegistrySourceKind.External"/> вручну не
/// змінюються (<c>D-211</c>): master — AF (<c>D-49</c>), дані приходять синком.
/// </summary>
/// <remarks>
/// ⛔ Виклик — у КОЖНОМУ користувацькому писачі записів (upsert, видалення, вікно чинності,
/// імпорт CSV — і прев'ю, і застосування, пакет RT-14 — і <c>dryRun</c>), до будь-якої зміни.
/// Сторож <c>ExternalRegistryManualEditGuardTests</c> (Architecture) тримає це за IL.
/// <para>
/// ⚠ <see cref="RegistrySourceKind.Hybrid"/> і <see cref="RegistrySourceKind.Local"/> — дозволено.
/// Синк (<c>RegistrySyncJob</c> → <see cref="RegistryEntryWriter"/>) гарду не кличе: це і є
/// шлях master-даних у довідник.
/// </para>
/// <para>
/// ⚠ Код — наявний <c>ECR-REG-0409</c> (конфлікт стану, 409): повторювати запит марно, правка
/// робиться в AF. Нового коду не заведено.
/// </para>
/// </remarks>
public static class ExternalRegistryGuard
{
    /// <summary>Ключ каталогу відмови.</summary>
    public const string MessageKey = "err.ECR-REG-0409.externalSource";

    /// <summary>Відмовляє, якщо записи довідника синхронізуються з AF.</summary>
    /// <param name="definition">Довідник, записи якого змінює запит.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REG-0409</c>, <see cref="MessageKey"/>.</exception>
    public static void EnsureManualEditAllowed(RegistryDef definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.SourceKind != RegistrySourceKind.External)
        {
            return;
        }

        throw new BusinessRuleException(
            ErrorCodes.RegistryEntryInUse,
            $"Записи довідника «{definition.Code}» синхронізуються з AF: змінюйте їх в AF (D-211).",
            new Dictionary<string, object?>
            {
                ["messageKey"] = MessageKey,
                ["registryCode"] = definition.Code,
            });
    }
}
