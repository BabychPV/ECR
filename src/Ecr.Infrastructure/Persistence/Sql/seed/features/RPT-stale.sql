-- RPT:stale ── ru/kz позначки застарілого зрізу (ФВ-10.5); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'snapshots.stale', N'ru', N'Устарел'),
    (N'snapshots.staleHint', N'ru', N'После формирования этого среза проект и период были пересчитаны. Его числа сохранены в точности такими, какими были; чтобы увидеть текущие, сформируйте новый срез.'),
    (N'snapshots.stale', N'kz', N'Ескірген'),
    (N'snapshots.staleHint', N'kz', N'Бұл кесінді құрылғаннан кейін жоба мен кезең қайта есептелді. Оның сандары бұрынғыдай дәл сақталған; ағымдағыларын көру үшін жаңа кесінді құрыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- RPT:stale ── кінець секції ──

