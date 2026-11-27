using Ecr.Application.Errors;
using Ecr.Application.Permits.AirLimits;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Errors;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// B158 (лінія d): програш гонки двох однакових запитів (довідники, профілі імпорту, версії звітів, ролі) — та сама
/// відповідь, що й послідовна перевірка обробника, а не сирий <see cref="DbUpdateException"/> (голий 500).
/// </summary>
/// <remarks>
/// Спільна форма — check-then-insert: «чи вже є?» читається без блокування, обидва одночасні запити проходять перевірку, і
/// другого відбиває лише унікальний індекс. Кожен арм — лише СВІЙ індекс (за ім'ям у лапках: <c>'UQ_Role'</c> — префікс
/// <c>UQ_RoleAssignment_*</c>) і лише доданий/змінений рядок СВОГО типу.
/// <para>
/// ⚠ Де аргумент послідовної відмови — ключ ПЕРЕМОЖЦЯ (довідник: <c>{id}</c>; чернетка опису: <c>{rowVersion}</c>) або
/// поле, якого рядок не несе (група: <c>{code}</c> ролі), база його не каже. Тоді арм дає загальний <c>409 ECR-CELL-0409
/// concurrentChange</c>, а обробник на цей конфлікт перечитує переможця тією самою перевіркою й кидає повну послідовну
/// відмову (<c>CreateRegistryHandler</c>, <c>SaveRegistryDefinitionDraftHandler</c>, <c>AssignGroupRoleHandler</c>). Для
/// upsert-ів (рівні ієрархії, значення запису) послідовний повтор — успіх, і загальний 409 — та сама відповідь, що й на
/// будь-яку іншу одночасну зміну того самого стану.
/// </para>
/// </remarks>
internal static class DuplicateRefusalsAdmin
{
    /// <summary>Відмова для програшу гонки на одному з індексів лінії d; <c>null</c> — не наш випадок.</summary>
    public static EcrException? TryMap(DbUpdateException ex)
    {
        // d1. `POST /registries` (`CreateRegistryHandler`, `FindDefinitionAsync`): загальний 409; обробник перечитує
        // переможця і відповідає `ECR-REG-4091 registryCodeTaken {code, id}`. Реєстри `PRM_`/`PRMG_` мапляться раніше
        // (`UnitOfWork.TryMapDuplicateKey`); сюди ж — `AIR_AREA` розгортання AIR (загальний 409).
        if (SqlConflict.ViolatesIndex(ex, "'UQ_RegistryDef'")
            && ex.Entries.Any(e => e.State == EntityState.Added && e.Entity is RegistryDef))
        {
            return Concurrent("Довідник із цим кодом щойно завів інший запит.");
        }

        // d2. `PUT /registries/{code}/hierarchy`: рівні читаються до транзакції, тож дві однакові заміни додають ті самі
        // рівні обидві. Upsert — загальний 409, повторний PUT уже оновить наявні рівні.
        if ((SqlConflict.ViolatesIndex(ex, "'UQ_RegistryLevelDef_Code'") || SqlConflict.ViolatesIndex(ex, "'UQ_RegistryLevelDef_Ordinal'"))
            && ex.Entries.Any(e => e.State is EntityState.Added or EntityState.Modified && e.Entity is RegistryLevelDef))
        {
            return Concurrent("Рівні ієрархії цього довідника щойно змінив інший запит.");
        }

        // d3. Правка запису довідника, що вперше заповнює поле: значення запису читаються до транзакції (блокування опису
        // їх не покриває). Upsert — загальний 409, повторний запит уже оновить наявне значення.
        if (SqlConflict.ViolatesIndex(ex, "'UQ_RegistryValue'")
            && ex.Entries.Any(e => e.State == EntityState.Added && e.Entity is RegistryValue))
        {
            return Concurrent("Значення цього запису довідника щойно записав інший запит.");
        }

        // d4. `PUT /air-limits/import-profiles/{code}` без токена (новий профіль): та сама відмова, що й перевірка
        // `AirImportProfileHandler` — `ECR-REG-0409 profileCodeTaken {code}`.
        if (SqlConflict.ViolatesIndex(ex, "'UQ_RegistryImportProfile'")
            && ex.Entries.FirstOrDefault(e => e.State == EntityState.Added && e.Entity is RegistryImportProfile)
                is { Entity: RegistryImportProfile profile })
        {
            return new ConcurrencyConflictException(
                "ECR-REG-0409",
                $"Import profile {profile.Code} already exists.",
                new Dictionary<string, object?> { ["messageKey"] = AirLimitsKeys.ProfileCodeTaken, ["code"] = profile.Code });
        }

        // d5. Перше `PUT /registries/{code}/definition/draft`: загальний 409; обробник перечитує чернетку переможця і
        // відповідає `ECR-REG-0409 definitionDraftChanged {registryCode, rowVersion}`.
        if (SqlConflict.ViolatesIndex(ex, "'PK_RegistryDefinitionDraft'")
            && ex.Entries.Any(e => e.State == EntityState.Added && e.Entity is RegistryDefinitionDraft))
        {
            return Concurrent("Чернетку опису цього довідника щойно зберіг інший запит.");
        }

        // d7. `POST /security/group-assignments` (перелік без блокування): загальний 409; обробник перечитує призначення і
        // відповідає `ECR-SEC-0409 groupAssignmentExists {code, sid}` (код ролі рядок призначення не несе).
        if (SqlConflict.ViolatesIndex(ex, "'UQ_RoleAssignment_Sid'")
            && ex.Entries.Any(e => e.State == EntityState.Added && e.Entity is RoleAssignment))
        {
            return Concurrent("Цю роль цій групі щойно призначив інший запит.");
        }

        // d9. `PUT /roles/{id}/code` (`RoleLifecycle.EnsureCodeFreeAsync`): дві РІЗНІ ролі в один код →
        // `ECR-SEC-0409 roleCodeTaken {code}`. Створення ролі ловить той самий індекс у `UserStore.SaveRoleAsync`.
        if (SqlConflict.ViolatesIndex(ex, "'UQ_Role'")
            && ex.Entries.FirstOrDefault(e => e.State is EntityState.Added or EntityState.Modified && e.Entity is Role)
                is { Entity: Role role })
        {
            return new BusinessRuleException(
                ErrorCodes.SecurityConflict,
                $"Роль із кодом «{role.Code}» уже існує.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-SEC-0409.roleCodeTaken", ["code"] = role.Code });
        }

        return null;
    }

    /// <summary>Загальний конфлікт стану — той самий код і ключ, що й <c>DbUpdateConcurrencyException</c> в одиниці роботи.</summary>
    private static ConcurrencyConflictException Concurrent(string message)
        => new(
            "ECR-CELL-0409",
            message,
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CELL-0409.concurrentChange" });
}
