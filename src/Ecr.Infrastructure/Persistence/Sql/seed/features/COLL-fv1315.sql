-- COLL:fv1315 ── ru/kz залежності розкладу збору від іншого розкладу (ФВ-13.15); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.collectionScheduleDependencyNotFound', N'ru', N'Расписания, от которого должно зависеть это расписание, не существует.'),
    (N'err.ECR-REQ-0422.collectionScheduleDependencyNotFound', N'kz', N'Бұл жоспар тәуелді болуы тиіс жоспар жоқ.'),
    (N'err.ECR-REQ-0422.collectionScheduleDependencyOtherSource', N'ru', N'Расписание может зависеть только от расписания того же подключения.'),
    (N'err.ECR-REQ-0422.collectionScheduleDependencyOtherSource', N'kz', N'Жоспар тек сол қосылымның жоспарына тәуелді бола алады.'),
    (N'err.ECR-REQ-0422.collectionScheduleDependencyCycle', N'ru', N'Эта зависимость замкнула бы цикл: другое расписание уже зависит от этого.'),
    (N'err.ECR-REQ-0422.collectionScheduleDependencyCycle', N'kz', N'Бұл тәуелділік циклді тұйықтайды: екінші жоспар бұған әлдеқашан тәуелді.'),
    (N'jobs.collectionDependencyWaiting', N'ru', N'Сущность {sourceEntityId}: пропуск, ожидается успешный прогон расписания {dependsOn}'),
    (N'jobs.collectionDependencyWaiting', N'kz', N'{sourceEntityId} нысаны: өткізілді, {dependsOn} жоспарының сәтті іске қосылуы күтілуде')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:fv1315 ── кінець секції ──

