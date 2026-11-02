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
