using Ecr.Application.Ports;
using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>
/// Публікує версію шаблону. Це найважливіша операція конфігуратора: після неї
/// структура заморожена, а всі перевірки, які можна зробити наперед, уже
/// зроблені.
/// </summary>
/// <remarks>
/// Публікація або проходить цілком, або відхиляється з переліком проблем.
/// Часткова публікація неможлива за побудовою.
/// </remarks>
public sealed class PublishTemplateVersionHandler(
    IRepository<Domain.Entities.Configuration.TemplateVersion, int> versions,
    ITemplateVersionStore versionStore,
    IFormulaEngine formulaEngine,
    IMetadataCache metadataCache,
    IUnitCatalog unitCatalog,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    IAuditWriter audit,
    IUnitOfWork uow,
    IClock clock)
{
    /// <summary>Право на публікацію версії шаблону (`02-contracts.md` §9).</summary>
    public const string Permission = "Template.Publish";

    /// <summary>Виконує публікацію.</summary>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="userId">Хто публікує.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// Валідація не пройдена; у <c>Details</c> — перелік діагностик.
    /// </exception>
    public async Task PublishAsync(int templateVersionId, int userId, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var version = await versions.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        // Усі дванадцять перевірок із 02b §12 — синтаксис, резолвінг, типи,
        // ациклічність, розкриття діапазонів, сумісність одиниць.
        // ⚠ Обидва контексти передаються ЯВНО: без контексту типів перевірка №3
        // мовчки не виконувалася (Q-072), без контексту одиниць — перевірка
        // сумісності (ФВ-16.6). Значення за замовчуванням тут ховало б пропуск.
        var structure = PublishChecks.Snapshot(version);
        var catalogue = await unitCatalog.GetAsync(ct).ConfigureAwait(false);

        var diagnostics = PublishChecks.Run(
            version,
            formulaEngine,
            new SnapshotTypeContext(structure),
            new SnapshotUnitContext(structure, catalogue));

        // ⛔ Плюс дві перевірки ПРАВИЛ: суперечливі рівні на одній області
        // (`ФВ-5.10`) і обов'язкові колонки, яких не перевіряє ніхто
        // (`ФВ-5.11`). Обидві названі вимогами і не існували: `Run`
        // дивиться лише на формули, тому суперечність між правилами
        // виявлялася в рантаймі — коли оператор уже не може зберегти рядок
        // і не розуміє чому.
        //
        // ⚠ Результати ЗЛИВАЮТЬСЯ в один перелік: публікація або проходить
        // цілком, або відхиляється з усіма проблемами одразу. Окрема
        // відмова на правила означала б два кола виправлень замість одного.
        diagnostics = [.. diagnostics, .. PublishChecks.CheckRules(version)];

        if (diagnostics.Count > 0)
        {
            // Усі проблеми одразу, а не перша: інакше користувач публікував би
            // версію десятки разів, виправляючи по одній.
            throw new BusinessRuleException(
                "ECR-TMPL-0422",
                $"Публікацію відхилено: знайдено проблем — {diagnostics.Count}.",
                new Dictionary<string, object?>
                {
                    ["diagnostics"] = diagnostics
                        .Select(d => new DiagnosticInfo(d.Code, d.Message, d.Position, d.Length))
                        .ToList(),
                });
        }

        // ⛔ Граф залежностей фіксується САМЕ ТУТ і зберігається. Діапазони
        // при цьому вже розкриті в конкретні рядки: у рантаймі діапазонів не
        // існує (`B03` §4).
        //
        // До цього таблиця `cfg.FormulaDependency` не наповнювалася нічим, і
        // наслідок був найтихішим із можливих: граф порожній, каскадний
        // перерахунок не бачить похідних комірок, числа лишаються старими —
        // без жодної помилки на екрані (`A7-63`).
        var dependencies = PublishChecks.Dependencies(version, formulaEngine);
        await versionStore
            .ReplaceFormulaDependenciesAsync(templateVersionId, dependencies, ct)
            .ConfigureAwait(false);

        // Перехід стану. Кидає ECR-TMPL-0409, якщо версія вже опублікована;
        // виняток виходить назовні до SaveChanges, тому часткових змін немає.
        version.Publish(userId, clock.UtcNow);

        await audit.WritePublicationEventAsync(
            new PublicationEventRecord(
                clock.UtcNow, EntityType: "TemplateVersion", EntityId: templateVersionId,
                ResultDiffJson: null, ChangeReason: "Publish", ChangedByUserId: userId),
            ct).ConfigureAwait(false);

        // Аудит і зміна стану — в одній транзакції: подія публікації без
        // публікації (і навпаки) зробила б журнал недостовірним.
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Виводить версію з обігу — відкат без видалення (<c>ФВ-7.8</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Версія **не видаляється**: на неї посилаються проєкти, подані форми,
    /// зрізи звітності й аудит структурних змін. Зміст відкату — «більше не
    /// брати в нові проєкти», а не «стерти сліди».
    ///
    /// ⛔ Право те саме, що на публікацію: вивести з обігу — рішення тієї
    /// самої ваги, що й випустити. Окреме право тут означало б, що відкат
    /// може зробити той, кому не довірили публікацію.
    ///
    /// ⚠ Проєкти, прив'язані до цієї версії, працюють далі (<c>ФВ-1.2</c>):
    /// інакше відкат зупинив би заповнення форм посеред періоду.
    /// </remarks>
    /// <param name="templateVersionId">Версія.</param>
    /// <param name="userId">Хто виводить з обігу.</param>
    /// <param name="reason">Причина; потрапляє в журнал публікацій.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task DeprecateAsync(
        int templateVersionId, int userId, string reason, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var version = await versions.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        version.Deprecate(userId, clock.UtcNow);

        await audit.WritePublicationEventAsync(
            new PublicationEventRecord(
                clock.UtcNow, EntityType: "TemplateVersion", EntityId: templateVersionId,
                ResultDiffJson: null, ChangeReason: reason, ChangedByUserId: userId),
            ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        // Ключ кешу не змінився — змінився СТАН версії, а структура ні. Але
        // знімок несе і статус, і саме за ним конфігуратор вирішує, чи
        // пропонувати версію в нових проєктах.
        await metadataCache.InvalidateAsync(templateVersionId, ct).ConfigureAwait(false);
    }
}

/// <summary>Проблема публікації у відповіді API.</summary>
/// <remarks>
/// Окремий тип, а не <c>ExpressionDiagnostic</c>: у відповідь іде рівно те, що
/// потрібно конфігуратору для підсвічування — код, текст і межі фрагмента.
/// </remarks>
/// <param name="Code">Код помилки з каталогу.</param>
/// <param name="Message">Пояснення.</param>
/// <param name="Position">Зсув у тексті виразу.</param>
/// <param name="Length">Довжина проблемного фрагмента.</param>
public sealed record DiagnosticInfo(string Code, string Message, int Position, int Length);
