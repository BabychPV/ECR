-- TMPL:discard-draft ── ru/kz: відкинути чернетку версії шаблону; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0409.discardRequiresDraft', N'ru', N'Отбросить можно только черновик; версия {version} в состоянии {status}. Опубликованную версию выводят из обращения, а не удаляют.'),
    (N'err.ECR-TMPL-0409.discardRequiresDraft', N'kz', N'Тек нобайды алып тастауға болады; {version} нұсқасы {status} күйінде. Жарияланған нұсқа жойылмайды, айналымнан шығарылады.'),
    (N'err.ECR-TMPL-0409.discardVersionInUse', N'ru', N'Черновик {version} нельзя отбросить: на него ссылаются {usages}.'),
    (N'err.ECR-TMPL-0409.discardVersionInUse', N'kz', N'{version} нобайын алып тастауға болмайды: оған {usages} сілтейді.'),
    (N'version.discard', N'ru', N'Отбросить черновик'),
    (N'version.discard', N'kz', N'Нобайды алып тастау'),
    (N'version.discardTitle', N'ru', N'Отбросить черновик {version}?'),
    (N'version.discardTitle', N'kz', N'{version} нобайын алып тастау керек пе?'),
    (N'version.discardText', N'ru', N'Черновик и вся его структура (листы, таблицы, колонки, формулы, правила) будут удалены. Действие необратимо.'),
    (N'version.discardText', N'kz', N'Нобай және оның бүкіл құрылымы (парақтар, кестелер, бағандар, формулалар, ережелер) жойылады. Әрекетті қайтару мүмкін емес.'),
    (N'version.discarded', N'ru', N'Черновик отброшен'),
    (N'version.discarded', N'kz', N'Нобай алып тасталды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- TMPL:discard-draft ── кінець секції ──
-- TMPL:discard-draft ── DELETE /template-versions/{id}: відкинути чернетку; ru/kz — порцією TMPL:discard-draft вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0409.discardRequiresDraft', N'en', N'Only a draft can be discarded; version {version} is {status}. A published version is deprecated, not deleted.', 1),
    (N'err.ECR-TMPL-0409.discardVersionInUse', N'en', N'Draft {version} cannot be discarded: it is referenced by {usages}.', 1),
    (N'version.discard', N'en', N'Discard draft', 1),
    (N'version.discardTitle', N'en', N'Discard draft {version}?', 1),
    (N'version.discardText', N'en', N'The draft and its whole structure (sheets, tables, columns, formulas, rules) will be deleted. This cannot be undone.', 1),
    (N'version.discarded', N'en', N'Draft discarded', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- TMPL:discard-draft ── кінець секції ──
