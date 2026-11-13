-- DOC:migrate-locked-overrides ── ru/kz: V-7, ручні значення (D-461) на поданих/затверджених аркушах блокують перенос версії; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-DOC-0409.migrateLockedOverrides', N'ru', N'На поданных или утверждённых листах есть ручные значения вместо формул, которые целевая версия не разрешает: значений {lockedOverrides}, листов {lockedOverrideSheets}. Верните эти листы в работу, прежде чем переносить документы на другую версию шаблона.'),
    (N'err.ECR-DOC-0409.migrateLockedOverrides', N'kz', N'Тапсырылған немесе бекітілген парақтарда мақсатты нұсқа рұқсат етпейтін формулалардың орнына қолмен енгізілген мәндер бар: мәндер {lockedOverrides}, парақтар {lockedOverrideSheets}. Құжаттарды үлгінің басқа нұсқасына көшірмес бұрын бұл парақтарды жұмысқа қайтарыңыз.'),
    (N'documents.migrateRefusalLockedOverrides', N'ru', N'На поданных или утверждённых листах есть ручные значения, которые целевая версия не разрешает: сначала верните эти листы в работу.'),
    (N'documents.migrateRefusalLockedOverrides', N'kz', N'Тапсырылған немесе бекітілген парақтарда мақсатты нұсқа рұқсат етпейтін қолмен енгізілген мәндер бар: алдымен бұл парақтарды жұмысқа қайтарыңыз.'),
    (N'documents.migrateLockedOverrides', N'ru', N'Ручных значений вместо формул на поданных или утверждённых листах, которые заменила бы формула: {count}. Верните в работу листы:'),
    (N'documents.migrateLockedOverrides', N'kz', N'Тапсырылған немесе бекітілген парақтардағы формула ауыстыратын қолмен енгізілген мәндер: {count}. Мына парақтарды жұмысқа қайтарыңыз:')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- DOC:migrate-locked-overrides ── кінець секції ──
-- DOC:migrate-locked-overrides ── V-7: ручні значення (D-461) на поданих/затверджених аркушах блокують перенос версії; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-DOC-0409.migrateLockedOverrides', N'en', N'Submitted or approved sheets hold manual values instead of formulas that the target version does not allow: {lockedOverrides} value(s) on {lockedOverrideSheets} sheet(s). Return these sheets to work before moving the documents to another template version.', 1),
    (N'documents.migrateRefusalLockedOverrides', N'en', N'Submitted or approved sheets hold manual values that the target version does not allow: return these sheets to work first.', 1),
    (N'documents.migrateLockedOverrides', N'en', N'Manual values instead of formulas on submitted or approved sheets that the formula would replace: {count}. Return these sheets to work:', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- DOC:migrate-locked-overrides ── кінець секції ──
