-- tools/e2e/source-impact-binding.sql
-- B80 N5 (D-554): зв'язка джерела `cfg.DocumentSourceBinding` для `src/Ecr.Web/e2e/sourceImpact.spec.ts`.
--
-- ⛔ Лише ТИМЧАСОВА база стенда (`tools/e2e-stand.ps1`): продуктовий сід не змінюється, тестових ендпоінтів у продукті
-- немає. API не вміє створювати зв'язку й задавати її політику (`Warn / ConfirmWithReason / Block`) — лише перемикати
-- стан-ворота й біти (`PUT /document-source-bindings/{id}/source-state|flags`), тож рядок кладеться сирим SQL.
-- ⚠ Сторож: без позначки `Ecr_E2E_Temp` на базі скрипт зупиняється — чужу базу він не чіпає.
--
-- Змінні sqlcmd: $(BindingCode) — код зв'язки, $(SourceTemplateCode) — код шаблону-джерела (створений API),
-- $(RegistryCode) — код довідника (створений API), $(Policy) — 0 None, 1 Warn, 2 ConfirmWithReason, 3 Block.
-- Зв'язка: відображення складу HSE400 (`MappingKind = 1`), стан `ApprovedOnly`, читання з документа й перерахунок
-- залежних увімкнено. Споживачів не перелічуємо: їх дає граф залежностей (формула шаблону-споживача читає довідник).
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;

IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 0 AND name = N'Ecr_E2E_Temp')
    THROW 50554, 'source-impact-binding: the database is not an e2e stand database (no Ecr_E2E_Temp mark)', 1;

DECLARE @template int = (SELECT Id FROM cfg.Template WHERE Code = N'$(SourceTemplateCode)');
DECLARE @registry int = (SELECT Id FROM cfg.RegistryDef WHERE Code = N'$(RegistryCode)');
IF @template IS NULL THROW 50554, 'source-impact-binding: source template not found', 1;
IF @registry IS NULL THROW 50554, 'source-impact-binding: registry not found', 1;

INSERT INTO cfg.DocumentSourceBinding
    (Code, SourceTemplateId, RegistryDefId, MappingKind, SourceTableCodes, Policy, SourceState,
     ReadFromDocument, RecalculateDependents, UpdatedAt, UpdatedByUserId)
VALUES
    (N'$(BindingCode)', @template, @registry, 1, NULL, $(Policy), 1, 1, 1, NULL, NULL);
