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

