using Ecr.Application.Documents;
using Ecr.Application.Templates;
using Ecr.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Application;

/// <summary>Реєстрація use-cases застосунку в контейнері.</summary>
/// <remarks>
/// ⚠ Файла немає ні в дереві `05-skeleton.md` §1, ні в `05c`, але
/// <c>Program.cs</c> (`05h`) викликає <c>AddEcrApplication()</c> — без нього
/// не збирається `Ecr.Api` (Q-015).
///
/// Тут реєструються <b>лише</b> обробники і доменні служби застосунку.
/// Реалізацій портів тут немає і бути не може: вони живуть в
/// <c>Ecr.Infrastructure</c>, і саме ця межа робить застосунок тестованим
/// без бази (`05-skeleton.md` §4).
/// </remarks>
public static class DependencyInjection
{
    /// <summary>Додає обробники use-cases, валідацію і перерахунок.</summary>
    /// <remarks>
    /// ⚠ Реєстрація <b>поіменна</b>, а не скануванням збірки:
    /// <c>Assembly.Load</c> і <c>Activator.CreateInstance</c> за рядком
    /// заборонені архітектурним правилом 8 (tz/03 §3.3). Ціна — цей список
    /// треба доповнювати руками; вигода — список видно, і забутий обробник
    /// падає при старті, а не на першому запиті користувача.
    ///
    /// Рушій виразів з'явився на Етапі 2, тому <c>ValidationEngine</c>,
    /// <c>RecalculationService</c>, <c>PatchCellsHandler</c>,
    /// <c>ValidateDocumentHandler</c> і <c>PublishTemplateVersionHandler</c>
    /// зареєстровані тут. Раніше їх не було саме тому, що в Development
    /// контейнер перевіряється при побудові, і застосунок не стартував би
    /// взагалі (`Q-051`).
    /// </remarks>
    public static IServiceCollection AddEcrApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Метадані шаблонів (модуль 1.7)
        services.AddScoped<CreateTemplateVersionHandler>();
        services.AddScoped<CloneTemplateVersionHandler>();
        services.AddScoped<GetTemplateStructureHandler>();
        services.AddScoped<GetAccessMatrixHandler>();
        services.AddScoped<Projects.ListPeriodPoliciesHandler>();
        services.AddScoped<Workflow.GetApprovalRouteHandler>();
        services.AddScoped<Workflow.ReplaceApprovalRouteHandler>();
        services.AddScoped<Security.ListUserRolesHandler>();
        services.AddScoped<Security.ReplaceUserRolesHandler>();
        services.AddScoped<Security.SetUserEmailHandler>();
        services.AddScoped<DiffTemplateVersionsHandler>();
        services.AddScoped<PatchPresentationHandler>();

        // Шаблони і проєкти: переліки і створення (модуль 1.7)
        services.AddScoped<Templates.ListTemplatesHandler>();
        services.AddScoped<Templates.CreateTemplateHandler>();
        services.AddScoped<Templates.ListTemplateVersionsHandler>();
        services.AddScoped<Projects.ListProjectsHandler>();
        services.AddScoped<Projects.CreateProjectHandler>();
        services.AddScoped<Projects.ActivateProjectHandler>();
        services.AddScoped<Projects.ArchiveProjectHandler>();

        // Документи і комірки (модуль 1.8)
        services.AddScoped<CreateDocumentHandler>();
        services.AddScoped<ListDocumentsHandler>();
        services.AddScoped<GetDocumentHandler>();
        services.AddScoped<GetTableSliceHandler>();
        services.AddScoped<CreateRowHandler>();

        // Вирази, валідація і перерахунок (модулі 2.6–2.8)
        services.AddScoped<Validation.ValidationEngine>();
        services.AddScoped<Recalculation.RecalculationService>();
        services.AddScoped<ValidateDocumentHandler>();
        services.AddScoped<Templates.PublishTemplateVersionHandler>();

        // Безпека (модулі 3.1–3.3)
        services.AddScoped<Security.EnsureBootstrapAdminHandler>();
        services.AddScoped<Security.DisableBootstrapAdminHandler>();
        services.AddScoped<Security.ChangePasswordHandler>();
        services.AddScoped<Security.LoginHandler>();
        services.AddScoped<Security.GetCurrentUserHandler>();
        services.AddScoped<Security.StartSimulationHandler>();
        services.AddScoped<Security.EndSimulationHandler>();

        // Безпека: ролі, користувачі, аудит (модулі 3.2, 3.9)
        services.AddScoped<Security.ListRolesHandler>();
        services.AddScoped<Security.ListResourceGrantsHandler>();
        services.AddScoped<Security.ReplaceResourceGrantsHandler>();
        services.AddScoped<Security.SetReceivesAlertsHandler>();
        services.AddScoped<Security.CreateRoleHandler>();
        services.AddScoped<Security.ListUsersHandler>();
        services.AddScoped<Security.CreateUserHandler>();
        services.AddScoped<Audit.GetCellChangesHandler>();
        services.AddScoped<Projects.CloneProjectHandler>();

        // Періоди (модуль 3.4)
        services.AddScoped<Periods.BuildPeriodCalendarHandler>();
        services.AddScoped<Periods.SetCurrentPeriodHandler>();
        services.AddScoped<Periods.ReopenPeriodHandler>();
        services.AddScoped<Periods.GetPeriodCalendarHandler>();

        // Робочий процес (модулі 3.4–3.6)
        // ⚠ Проведення стану аркушів у зрізи звітності — окрема залежність
        // обох обробників: питання «що тепер зі зрізом» одне, і відповідь на
        // нього має бути одна (`H-23b`).
        services.AddScoped<Reporting.ReportSnapshotSync>();
        services.AddScoped<Workflow.SubmitSheetHandler>();
        services.AddScoped<Workflow.ApproveSheetHandler>();
        services.AddScoped<Workflow.ReopenDocumentHandler>();

        // Локалізація (модуль 3.7)
        services.AddScoped<Localization.GetUiStringsHandler>();

        // Етап 4 — довідники та одиниці.
        services.AddSingleton<Registries.RegistryResolver>();
        services.AddScoped<Registries.ListRegistriesHandler>();
        services.AddScoped<Registries.GetRegistryEntriesHandler>();
        services.AddScoped<Registries.UpsertRegistryEntryHandler>();
        services.AddScoped<Registries.SetEntryValidityHandler>();
        services.AddScoped<Registries.SwitchRegistrySourceHandler>();
        services.AddScoped<Registries.DeleteRegistryEntryHandler>();
        services.AddScoped<Units.ConvertUnitHandler>();

        // Редактор виразів (`ФВ-9.15a`): перевірка тексту і склад мови.
        services.AddScoped<Expressions.ValidateExpressionHandler>();
        services.AddScoped<Expressions.GetExpressionMetadataHandler>();
        services.AddScoped<Units.ListUnitsHandler>();
        services.AddScoped<Calculations.ListMethodologiesHandler>();
        services.AddScoped<Calculations.PublishMethodologyHandler>();
        services.AddScoped<Calculations.SimulateMethodologyHandler>();
        services.AddScoped<Calculations.RunCalculationHandler>();
        services.AddScoped<Localization.SetUiStringHandler>();

        // ⚠ PatchCellsHandler і RecalculateDocumentHandler зареєстровані з
        // Етапу 3. Раніше їх не було через IBackgroundJobScheduler без
        // реалізації (`Q-051`), і це виявилося гіршим за відсутність черги:
        // без реєстрації DocumentsController не створювався взагалі, і 500
        // отримували ВСІ його ендпоінти. Тепер планувальник зареєстрований як
        // явна відмова ECR-SYS-0503 (QuartzJobScheduler без фабрики), а справжня
        // черга приходить на Етапі 5 разом із рішенням щодо LGPL (D-09).
        services.AddScoped<Documents.PatchCellsHandler>();
        services.AddScoped<Documents.RecalculateDocumentHandler>();

        // Етап 5: обмін із Excel, звітність, інтеграція, стан задач.
        services.AddScoped<Documents.GetDocumentTablesHandler>();
        services.AddScoped<Documents.ExportDocumentHandler>();
        services.AddScoped<Documents.DownloadExportHandler>();
        services.AddScoped<Documents.PreviewImportHandler>();
        services.AddScoped<Documents.ApplyImportHandler>();
        services.AddScoped<Reporting.ListReportSnapshotsHandler>();
        services.AddScoped<Reporting.BuildReportSnapshotHandler>();
        services.AddScoped<Integration.ListSourceEntitiesHandler>();
        services.AddScoped<Integration.CollectFromSourceHandler>();
        services.AddScoped<Integration.GetJobStatusHandler>();

        // Доменні служби без стану
        services.AddSingleton<ChangeClassifier>();

        return services;
    }
}
