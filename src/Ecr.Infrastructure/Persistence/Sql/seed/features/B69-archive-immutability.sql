-- B69:archive-immutability ── ru/kz: B69 (G1): архівовані дані не змінюються — відмови перерахунку, повернення аркуша й архівації з діючими винятками документа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-4221.projectArchived', N'ru', N'Проект в архиве или архивируется: его данные не пересчитываются.'),
    (N'err.ECR-CALC-4221.projectArchived', N'kz', N'Жоба мұрағатта немесе мұрағатталуда: оның деректері қайта есептелмейді.'),
    (N'err.ECR-PRD-4223.projectArchived', N'ru', N'Проект документа в архиве: листы в нём не возвращаются в работу.'),
    (N'err.ECR-PRD-4223.projectArchived', N'kz', N'Құжаттың жобасы мұрағатта: ондағы парақтар жұмысқа қайтарылмайды.'),
    (N'err.ECR-PRD-0409.openDocumentReopens', N'ru', N'В проекте {projectId} месяцы открыты для отдельных документов ({periods}): закройте эти открытия или дождитесь их срока, затем архивируйте.'),
    (N'err.ECR-PRD-0409.openDocumentReopens', N'kz', N'{projectId} жобасында айлар жекелеген құжаттар үшін ашық ({periods}): бұл ашуларды жабыңыз немесе мерзімін күтіңіз, содан кейін мұрағаттаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B69:archive-immutability ── кінець секції ──
-- B69:archive-immutability ── B69 (G1): архівовані дані не змінюються застосунком; ru/kz — порцією B69:archive-immutability вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-4221.projectArchived', N'en', N'The project is archived or being archived: its data is not recalculated.', 1),
    (N'err.ECR-PRD-4223.projectArchived', N'en', N'The document''s project is archived: its sheets cannot be returned to work.', 1),
    (N'err.ECR-PRD-0409.openDocumentReopens', N'en', N'Project {projectId} has months reopened for individual documents ({periods}): close those reopenings or wait until they expire, then archive.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B69:archive-immutability ── кінець секції ──
