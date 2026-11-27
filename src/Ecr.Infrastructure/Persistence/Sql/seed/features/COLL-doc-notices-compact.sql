-- COLL:doc-notices-compact ── ru/kz: повідомлення над сіткою документа одним рядком — чип перевідкритих місяців, смуга проблем розрахунку (b14, lane/integ-doc-notices-compact) ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.notices.reopened', N'ru', N'Переоткрыто: {ranges}'),
    (N'document.notices.reopened', N'kz', N'Қайта ашылды: {ranges}'),
    (N'document.notices.reopenedRange', N'ru', N'{months} · до {until}'),
    (N'document.notices.reopenedRange', N'kz', N'{months} · {until} дейін'),
    (N'document.notices.calcTitle', N'ru', N'Расчёт: строки не учтены в {months}'),
    (N'document.notices.calcTitle', N'kz', N'Есеп: {months} ішінде жолдар есепке алынбады'),
    (N'document.notices.calcTitleBlocked', N'ru', N'Расчёт завершён с проблемами в {months} · подача может быть отклонена'),
    (N'document.notices.calcTitleBlocked', N'kz', N'{months} ішінде есеп ақаулармен аяқталды · жіберу қабылданбауы мүмкін'),
    (N'document.notices.details', N'ru', N'Подробнее'),
    (N'document.notices.details', N'kz', N'Толығырақ'),
    (N'document.notices.reason', N'ru', N'Причина'),
    (N'document.notices.reason', N'kz', N'Себеп'),
    (N'document.notices.kind.sourceNotApproved', N'ru', N'источник не утверждён'),
    (N'document.notices.kind.sourceNotApproved', N'kz', N'дереккөз бекітілмеген'),
    (N'document.notices.kind.sourceChanged', N'ru', N'источник изменён'),
    (N'document.notices.kind.sourceChanged', N'kz', N'дереккөз өзгерді'),
    (N'document.notices.kind.sourceOther', N'ru', N'проблема источника'),
    (N'document.notices.kind.sourceOther', N'kz', N'дереккөз мәселесі'),
    (N'document.notices.kind.hse400', N'ru', N'нет состава HSE400'),
    (N'document.notices.kind.hse400', N'kz', N'HSE400 құрамы жоқ'),
    (N'document.notices.kind.formula', N'ru', N'ошибка формулы'),
    (N'document.notices.kind.formula', N'kz', N'формула қатесі'),
    (N'document.notices.kind.blocked', N'ru', N'не обновлено'),
    (N'document.notices.kind.blocked', N'kz', N'жаңартылмады'),
    (N'document.notices.kind.missingInputs', N'ru', N'нет входных данных'),
    (N'document.notices.kind.missingInputs', N'kz', N'кіріс деректері жоқ'),
    (N'document.notices.kind.rejected', N'ru', N'категория не определена'),
    (N'document.notices.kind.rejected', N'kz', N'санат анықталмады'),
    (N'document.notices.kind.unmatched', N'ru', N'нет правила расчёта'),
    (N'document.notices.kind.unmatched', N'kz', N'есеп ережесі жоқ'),
    (N'document.notices.kind.stepError', N'ru', N'ошибка шага методики'),
    (N'document.notices.kind.stepError', N'kz', N'әдістеме қадамының қатесі'),
    (N'document.notices.kind.inactive', N'ru', N'неактивные'),
    (N'document.notices.kind.inactive', N'kz', N'белсенді емес')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:doc-notices-compact ── кінець секції ──

-- COLL:doc-notices-compact ── en; ru/kz - порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.notices.reopened', N'en', N'Reopened: {ranges}', 1),
    (N'document.notices.reopenedRange', N'en', N'{months} · until {until}', 1),
    (N'document.notices.calcTitle', N'en', N'Calculation: rows not included in {months}', 1),
    (N'document.notices.calcTitleBlocked', N'en', N'Calculation finished with problems in {months} · submission may be refused', 1),
    (N'document.notices.details', N'en', N'Details', 1),
    (N'document.notices.reason', N'en', N'Reason', 1),
    (N'document.notices.kind.sourceNotApproved', N'en', N'source not approved', 1),
    (N'document.notices.kind.sourceChanged', N'en', N'source changed', 1),
    (N'document.notices.kind.sourceOther', N'en', N'source issue', 1),
    (N'document.notices.kind.hse400', N'en', N'no HSE400 composition', 1),
    (N'document.notices.kind.formula', N'en', N'formula failed', 1),
    (N'document.notices.kind.blocked', N'en', N'not updated', 1),
    (N'document.notices.kind.missingInputs', N'en', N'missing input', 1),
    (N'document.notices.kind.rejected', N'en', N'category not set', 1),
    (N'document.notices.kind.unmatched', N'en', N'no calculation rule', 1),
    (N'document.notices.kind.stepError', N'en', N'method step failed', 1),
    (N'document.notices.kind.inactive', N'en', N'inactive', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:doc-notices-compact ── кінець секції ──
