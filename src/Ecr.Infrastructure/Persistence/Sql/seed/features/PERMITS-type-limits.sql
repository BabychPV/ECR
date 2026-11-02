-- PERMITS: type-limits ── ru/kz: межі розміру визначення типу (аудит A, PA-A-06); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0422.tooManyTables', N'ru', N'В типе разрешения может быть не более {max} таблиц.'),
    (N'err.ECR-PRM-0422.tooManyTables', N'kz', N'Рұқсат түрінде {max} кестеден артық болмауы тиіс.'),
    (N'err.ECR-PRM-0422.tooManyColumns', N'ru', N'В таблице «{tableCode}» может быть не более {max} столбцов.'),
    (N'err.ECR-PRM-0422.tooManyColumns', N'kz', N'«{tableCode}» кестесінде {max} бағаннан артық болмауы тиіс.'),
    (N'err.ECR-PRM-0422.tooManyColumnsTotal', N'ru', N'Во всех таблицах типа разрешения вместе может быть не более {max} столбцов.'),
    (N'err.ECR-PRM-0422.tooManyColumnsTotal', N'kz', N'Рұқсат түрінің барлық кестелеріндегі бағандар саны жиынтығында {max}-ден аспауы тиіс.'),
    (N'err.ECR-PRM-0422.textTooLong', N'ru', N'Название «{item}» не должно быть длиннее {max} символов.'),
    (N'err.ECR-PRM-0422.textTooLong', N'kz', N'«{item}» атауы {max} таңбадан ұзын болмауы тиіс.'),
    (N'err.ECR-PRM-0422.draftTooLarge', N'ru', N'Черновик типа разрешения больше {max} символов: его нельзя сохранить или прочитать. Сократите длинные списки и выражения или удалите тип.'),
    (N'err.ECR-PRM-0422.draftTooLarge', N'kz', N'Рұқсат түрінің жобасы {max} таңбадан үлкен: оны сақтауға немесе оқуға болмайды. Ұзын тізімдер мен өрнектерді қысқартыңыз немесе түрді жойыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: type-limits ── кінець секції ──
-- PERMITS: type-limits ── en: межі розміру визначення типу (ECR-PRM-0422 tooMany*, textTooLong; аудит A, PA-A-06); ru/kz — порцією PERMITS: type-limits нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-0422.tooManyTables', N'en', N'A permit type can have at most {max} tables.', 1),
    (N'err.ECR-PRM-0422.tooManyColumns', N'en', N'Table "{tableCode}" can have at most {max} columns.', 1),
    (N'err.ECR-PRM-0422.tooManyColumnsTotal', N'en', N'A permit type can have at most {max} columns in all its tables together.', 1),
    (N'err.ECR-PRM-0422.textTooLong', N'en', N'The name of "{item}" must not be longer than {max} characters.', 1),
    (N'err.ECR-PRM-0422.draftTooLarge', N'en', N'The permit type draft is larger than {max} characters and cannot be saved or read. Remove long lists or expressions, or delete the type.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: type-limits ── кінець секції ──
