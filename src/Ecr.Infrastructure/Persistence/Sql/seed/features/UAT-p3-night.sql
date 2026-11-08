-- UAT:p3-night ── ru/kz: підтвердження «Submit: <місяць>» ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.submitConfirmTitle', N'ru', N'Подать {period}?'),
    (N'workflow.submitConfirmHint', N'ru', N'Лист за {period} будет отправлен на согласование. Проверьте, что это нужный месяц: он может отличаться от месяца на экране.'),
    (N'workflow.submitConfirmTitle', N'kz', N'{period} беру керек пе?'),
    (N'workflow.submitConfirmHint', N'kz', N'{period} парағы келісуге жіберіледі. Бұл қажетті ай екенін тексеріңіз: ол экрандағы айдан өзгеше болуы мүмкін.'),
    (N'grid.totalsReadOnlyHint', N'ru', N'Строка итогов считается из строк выше, редактировать её нельзя.'),
    (N'grid.totalsReadOnlyHint', N'kz', N'Қорытынды жол жоғарыдағы жолдардан есептеледі, оны өңдеуге болмайды.'),
    (N'documents.summaryScope', N'ru', N'{period} · {projects}:'),
    (N'documents.summaryScopeYear', N'ru', N'{year} год'),
    (N'documents.summaryScopeAllProjects', N'ru', N'все доступные проекты'),
    (N'documents.summaryScopeOneProject', N'ru', N'выбранный проект'),
    (N'campaign.countersScope', N'ru', N'{period} · все проекты кампании'),
    (N'documents.summaryScope', N'kz', N'{period} · {projects}:'),
    (N'documents.summaryScopeYear', N'kz', N'{year} жыл'),
    (N'documents.summaryScopeAllProjects', N'kz', N'барлық қолжетімді жобалар'),
    (N'documents.summaryScopeOneProject', N'kz', N'таңдалған жоба'),
    (N'campaign.countersScope', N'kz', N'{period} · науқанның барлық жобалары')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- UAT:p3-night ── кінець секції ──
-- UAT:p3-night ── підтвердження «Submit: <місяць>» (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'workflow.submitConfirmTitle', N'en', N'Submit {period}?', 1),
    (N'workflow.submitConfirmHint', N'en', N'The sheet for {period} will be sent for approval. Check that this is the month you mean: it may differ from the month on screen.', 1),
    (N'grid.totalsReadOnlyHint', N'en', N'The total row is calculated from the rows above and cannot be edited.', 1),
    (N'documents.summaryScope', N'en', N'{period} · {projects}:', 1),
    (N'documents.summaryScopeYear', N'en', N'Year {year}', 1),
    (N'documents.summaryScopeAllProjects', N'en', N'all projects you can see', 1),
    (N'documents.summaryScopeOneProject', N'en', N'selected project', 1),
    (N'campaign.countersScope', N'en', N'{period} · all projects in the campaign', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- UAT:p3-night ── кінець секції ──
