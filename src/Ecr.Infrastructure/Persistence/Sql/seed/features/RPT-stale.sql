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

-- RPT:stale ── позначка застарілого зрізу в переліку (ФВ-10.5, `SnapshotStaleBadge.tsx`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'snapshots.stale',                   N'en', N'Outdated', 1),
    (N'snapshots.staleHint',               N'en', N'The project and period were recalculated after this snapshot was built. Its numbers are kept exactly as they were; build a new snapshot to see the current ones.', 1)
    -- ru/kz — окремою порцією `RPT:stale` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- RPT:stale ── кінець секції ──
