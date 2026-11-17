// src/Ecr.Application/Registries/CreateRegistryHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries;

/// <summary>
/// Заведення довідника-контейнера **з нуля**, без жодного поля (директива
/// №11, T4).
/// </summary>
/// <remarks>
/// ⛔ Доти <c>POST /registries</c> не існувало взагалі: `RegistriesController`
/// умів лише читати перелік і правити ОПИС наявного довідника
/// (<see cref="SaveRegistryDefinitionHandler"/>), а сам довідник — тобто
/// перший рядок <c>cfg.RegistryDef</c> — заводив лише офлайновий seed. Отже
/// новий довідник, якого немає в seed, не міг з'явитися в системі жодним
/// шляхом, доступним людині.
///
/// ⛔ Право те саме, що на <c>PUT …/{code}/definition</c> —
/// <c>Registry.EditDefinition</c>, а не окреме. Заведення довідника і зміна
/// складу його полів — той самий клас рішення («що довідник узагалі описує»),
/// і роздавати для нього окреме право означало б розрізняти дві дії, які
/// ухвалює одна й та сама людина.
/// </remarks>
public sealed class CreateRegistryHandler(
    IRegistryStore registries,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на заведення довідника (`02-contracts.md` §9).</summary>
    public const string Permission = "Registry.EditDefinition";

    /// <summary>Ключ відмови «код зайнятий» (з <c>{id}</c> зайнятого довідника).</summary>
    public const string CodeTakenKey = "err.ECR-REG-4091.registryCodeTaken";

    /// <summary>Те саме без <c>{id}</c>: зайнятий довідник закритий читачеві забороною (L5-08).</summary>
    public const string CodeTakenHiddenKey = "err.ECR-REG-4091.registryCodeTakenHidden";

    /// <summary>Заводить довідник без жодного поля.</summary>
    /// <param name="code">Код, унікальний серед довідників.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="isTemporal">Чи мають майбутні записи вікно дії.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Щойно заведений довідник — без полів і без зв'язків.</returns>
    /// <exception cref="BusinessRuleException">Код уже зайнято — <c>ECR-REG-4091</c>.</exception>
    public async Task<RegistryDefDto> HandleAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        bool isTemporal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);

        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        return await ApplyAsync(code, name, isTemporal, profile, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ✎ AIRSYS02: the same creation for an author already checked by the caller, or for the SYSTEM at start
    /// (<c>NaeTreeProvisioner.EnsureAsSystemAsync</c>). <c>internal</c>: no controller can reach it (Ecr.Api does not see internals).
    /// </summary>
    /// <param name="code">Код, унікальний серед довідників.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="isTemporal">Чи мають майбутні записи вікно дії.</param>
    /// <param name="profile">Профіль автора (заборони довідників); у системи — без заборон.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<RegistryDefDto> ApplyAsync(
        string code,
        IReadOnlyDictionary<string, string> name,
        bool isTemporal,
        Security.AccessProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(profile);

        var registryCode = EcrCode.Create(code);

        // PRMG: префікс `PRMG_` зарезервований за прихованими реєстрами груп типів дозволів.
        Permits.Groups.PermitGroupRegistryGuard.RequireNotReserved(registryCode.Value);

        // ⚠ Унікальність коду перевіряється ТУТ так само, як у
        // `CreateMethodologyHandler`: код — те, чим на довідник посилаються
        // поля-посилання інших довідників (`RefRegistryDefId`) і колонки
        // шаблону (`Lookup`), і мовчазний другий довідник із тим самим кодом
        // зробив би це посилання неоднозначним.
        await RefuseIfTakenAsync(registryCode.Value, profile, ct).ConfigureAwait(false);

        var definition = new RegistryDef(
            registryCode,
            new LocalizedText(new Dictionary<string, string>(name, StringComparer.OrdinalIgnoreCase)),
            isTemporal);

        registries.AddDefinition(definition);

        try
        {
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException)
        {
            // ⛔ B158 d1. Перевірка вище читає без блокування: два одночасні запити проходять її обидва, і другого
            // відбиває `UQ_RegistryDef` (арм `DuplicateRefusalsAdmin` дає загальний 409). `{id}` переможця база не каже —
            // перечитуємо його тією самою перевіркою, і клієнт бачить ту саму відмову, що й послідовний повтор; код не
            // зайнятий (інший конфлікт) — загальний 409 як є.
            await RefuseIfTakenAsync(registryCode.Value, profile, ct).ConfigureAwait(false);
            throw;
        }

        return Map(definition);
    }

    /// <summary>Код зайнятий — <c>ECR-REG-4091 registryCodeTaken</c> (або <c>…Hidden</c> без <c>{id}</c>, L5-08).</summary>
    private async Task RefuseIfTakenAsync(string code, Security.AccessProfile profile, CancellationToken ct)
    {
        var clash = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false);
        if (clash is not null)
        {
            // ⛔ L5-08: ідентифікатор довідника, закритого цьому читачеві явною забороною, не віддається —
            // ні в тексті, ні в details. Сама відмова лишається: код зайнятий, і інакше другий довідник
            // із таким кодом не завести. (Те, що код зайнятий, людина дізнається з самого запиту — це не
            // витік; витік — Id, за яким можна адресувати довідник.)
            var hidden = RegistryAccess.IsDenied(profile, clash.Id);
            var details = new Dictionary<string, object?>
            {
                ["messageKey"] = hidden ? CodeTakenHiddenKey : CodeTakenKey,
                ["code"] = code,
            };
            if (!hidden)
            {
                details["id"] = clash.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            throw new BusinessRuleException(
                "ECR-REG-4091",
                hidden
                    ? $"Довідник «{code}» уже існує: "
                      + "код — те, чим на нього посилаються поля-довідники і колонки шаблону."
                    : $"Довідник «{code}» уже існує (ідентифікатор {clash.Id}): "
                      + "код — те, чим на нього посилаються поля-довідники і колонки шаблону.",
                details);
        }
    }

    /// <summary>Складає DTO щойно заведеного довідника — без полів.</summary>
    /// <param name="definition">Довідник.</param>
    public static RegistryDefDto Map(RegistryDef definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new RegistryDefDto(
            definition.Id,
            definition.Code,
            definition.NameL10n,

            // Щойно заведений довідник поля-посилання на себе мати не може —
            // ієрархічність з'являється лише після додавання такого поля
            // через конструктор (`ФВ-8.12`).
            IsHierarchical: false,
            definition.IsTemporal,
            definition.SourceKind,
            Fields: [],
            DefinitionVersion: definition.DefinitionVersion);
    }
}
