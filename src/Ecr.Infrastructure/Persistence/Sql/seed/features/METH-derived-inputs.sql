-- METH:derived-inputs ── D-578: виведені обов'язкові входи (поля, які читає скрипт) і виняток «Optional» у редакторі методології (клієнт) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'methodologies.severityOptional', N'en', N'Optional (may be empty)', 1),
    (N'methodologies.derivedInputs', N'en', N'Read by the script (required automatically)', 1),
    (N'methodologies.derivedInputsHint', N'en', N'Every field the script reads is required to submit the sheet in the rows this methodology applies to. Saving a cell is not blocked. To relax a field, add it to the list above as Warn or Optional.', 1),
    (N'methodologies.derivedUnavailable', N'en', N'The automatic list is not available for this version (the script was not compiled or its fields were not recorded). Only the list above applies.', 1),
    (N'methodologies.derivedField', N'en', N'Field', 1),
    (N'methodologies.derivedRequirement', N'en', N'Requirement', 1),
    (N'methodologies.derivedRequired', N'en', N'Required to submit', 1),
    (N'methodologies.derivedOtherMonths', N'en', N'Reads another month — not required yet', 1),
    (N'methodologies.severityHint', N'en', N'Block — an empty column blocks saving the row; Warn — only a warning; Optional — the field may stay empty (this also relaxes the automatic requirement of a field the script reads).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO

INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'methodologies.severityOptional', N'ru', N'Необязательное (может быть пустым)'),
    (N'methodologies.severityOptional', N'kz', N'Міндетті емес (бос болуы мүмкін)'),
    (N'methodologies.derivedInputs', N'ru', N'Читает скрипт (обязательные автоматически)'),
    (N'methodologies.derivedInputs', N'kz', N'Скрипт оқиды (автоматты түрде міндетті)'),
    (N'methodologies.derivedInputsHint', N'ru', N'Каждое поле, которое читает скрипт, обязательно для подачи листа в строках, к которым применяется эта методология. Сохранение ячейки не блокируется. Чтобы ослабить требование, добавьте поле в список выше как «Предупреждать» или «Необязательное».'),
    (N'methodologies.derivedInputsHint', N'kz', N'Скрипт оқитын әр өріс осы әдістеме қолданылатын жолдарда парақты тапсыру үшін міндетті. Ұяшықты сақтау бұғатталмайды. Талапты жұмсарту үшін өрісті жоғарыдағы тізімге «Ескерту» немесе «Міндетті емес» ретінде қосыңыз.'),
    (N'methodologies.derivedUnavailable', N'ru', N'Автоматический список для этой версии недоступен (скрипт не скомпилирован или его поля не записаны). Действует только список выше.'),
    (N'methodologies.derivedUnavailable', N'kz', N'Бұл нұсқа үшін автоматты тізім қолжетімсіз (скрипт компиляцияланбаған немесе оның өрістері жазылмаған). Тек жоғарыдағы тізім қолданылады.'),
    (N'methodologies.derivedField', N'ru', N'Поле'),
    (N'methodologies.derivedField', N'kz', N'Өріс'),
    (N'methodologies.derivedRequirement', N'ru', N'Требование'),
    (N'methodologies.derivedRequirement', N'kz', N'Талап'),
    (N'methodologies.derivedRequired', N'ru', N'Обязательно для подачи'),
    (N'methodologies.derivedRequired', N'kz', N'Тапсыру үшін міндетті'),
    (N'methodologies.derivedOtherMonths', N'ru', N'Читает другой месяц — пока не обязательно'),
    (N'methodologies.derivedOtherMonths', N'kz', N'Басқа айды оқиды — әзірге міндетті емес'),
    (N'methodologies.severityHint', N'ru', N'«Блокировать» — пустой столбец не даёт сохранить строку; «Предупреждать» — только предупреждение; «Необязательное» — поле может оставаться пустым (это ослабляет и автоматическое требование поля, которое читает скрипт).'),
    (N'methodologies.severityHint', N'kz', N'«Бұғаттау» — бос баған жолды сақтауға жол бермейді; «Ескерту» — тек ескерту; «Міндетті емес» — өріс бос қалуы мүмкін (бұл скрипт оқитын өрістің автоматты талабын да жұмсартады).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO

-- Змінений текст наявного ключа: епілог оновлює, лише поки в базі старе (випущене) значення.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'methodologies.severityHint', N'en',
     N'Whether an unfilled column blocks saving the row, or only warns while the save goes through.',
     N'Block — an empty column blocks saving the row; Warn — only a warning; Optional — the field may stay empty (this also relaxes the automatic requirement of a field the script reads).'),
    (N'methodologies.severityHint', N'ru',
     N'Блокирует ли незаполненный столбец сохранение строки или только предупреждает, а сохранение проходит.',
     N'«Блокировать» — пустой столбец не даёт сохранить строку; «Предупреждать» — только предупреждение; «Необязательное» — поле может оставаться пустым (это ослабляет и автоматическое требование поля, которое читает скрипт).'),
    (N'methodologies.severityHint', N'kz',
     N'Толтырылмаған баған жолды сақтауды бұғаттай ма, әлде сақтау өтіп, тек ескерту бере ме.',
     N'«Бұғаттау» — бос баған жолды сақтауға жол бермейді; «Ескерту» — тек ескерту; «Міндетті емес» — өріс бос қалуы мүмкін (бұл скрипт оқитын өрістің автоматты талабын да жұмсартады).')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- METH:derived-inputs ── кінець секції ──
