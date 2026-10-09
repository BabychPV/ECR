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

        var registryCode = EcrCode.Create(code);

        // ⚠ Унікальність коду перевіряється ТУТ так само, як у
        // `CreateMethodologyHandler`: код — те, чим на довідник посилаються
        // поля-посилання інших довідників (`RefRegistryDefId`) і колонки
        // шаблону (`Lookup`), і мовчазний другий довідник із тим самим кодом
        // зробив би це посилання неоднозначним.
        var clash = await registries.FindDefinitionAsync(registryCode.Value, ct).ConfigureAwait(false);
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
                ["code"] = registryCode.Value,
            };
            if (!hidden)
            {
                details["id"] = clash.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            throw new BusinessRuleException(
                "ECR-REG-4091",
                hidden
                    ? $"Довідник «{registryCode.Value}» уже існує: "
                      + "код — те, чим на нього посилаються поля-довідники і колонки шаблону."
                    : $"Довідник «{registryCode.Value}» уже існує (ідентифікатор {clash.Id}): "
                      + "код — те, чим на нього посилаються поля-довідники і колонки шаблону.",
                details);
        }

        var definition = new RegistryDef(
            registryCode,
            new LocalizedText(new Dictionary<string, string>(name, StringComparer.OrdinalIgnoreCase)),
            isTemporal);

        registries.AddDefinition(definition);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return Map(definition);
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
