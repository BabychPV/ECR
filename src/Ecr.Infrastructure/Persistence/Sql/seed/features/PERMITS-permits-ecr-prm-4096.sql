-- PERMITS: permits ── ECR-PRM-4096 ── ru/kz: стан дозволу не дозволяє дію; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-4096', N'ru', N'Состояние разрешения не допускает это действие'),
    (N'err.ECR-PRM-4096', N'kz', N'Рұқсаттың күйі бұл әрекетке жол бермейді'),
    (N'err.ECR-PRM-4096.changed', N'ru', N'Разрешение {id} изменили после того, как вы его открыли. Перечитайте его и повторите правку.'),
    (N'err.ECR-PRM-4096.changed', N'kz', N'{id} рұқсатын сіз ашқаннан кейін өзгертті. Оны қайта оқып, өзгерісті қайталаңыз.'),
    (N'err.ECR-PRM-4096.notEditable', N'ru', N'Разрешение {id} закончилось и больше не редактируется.'),
    (N'err.ECR-PRM-4096.notEditable', N'kz', N'{id} рұқсатының мерзімі аяқталды, енді өңделмейді.'),
    (N'err.ECR-PRM-4096.notDraft', N'ru', N'Разрешение {id} в состоянии {status}: это возможно только для черновика.'),
    (N'err.ECR-PRM-4096.notDraft', N'kz', N'{id} рұқсаты {status} күйінде: бұл тек жоба үшін мүмкін.'),
    (N'err.ECR-PRM-4096.inUse', N'ru', N'На разрешение {id} или его строки ссылаются ({count}): оно не удаляется.'),
    (N'err.ECR-PRM-4096.inUse', N'kz', N'{id} рұқсатына немесе оның жолдарына сілтемелер бар ({count}): ол жойылмайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: permits ── ECR-PRM-4096 ── ru/kz ── кінець секції ──

-- PERMITS: permits ── en: ECR-PRM-4096 і зміна/видалення дозволу (PM-04) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-4096', N'en', N'Permit state does not allow this', 1),
    (N'err.ECR-PRM-4096.changed', N'en', N'The permit {id} was changed after you opened it. Reload it and repeat your edit.', 1),
    (N'err.ECR-PRM-4096.notEditable', N'en', N'The permit {id} has expired and can no longer be edited.', 1),
    (N'err.ECR-PRM-4096.notDraft', N'en', N'The permit {id} is {status}: this is only possible for a draft.', 1),
    (N'err.ECR-PRM-4096.inUse', N'en', N'The permit {id} or its rows are referenced from elsewhere ({count}) and cannot be deleted.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: permits ── ECR-PRM-4096 ── кінець секції ──
