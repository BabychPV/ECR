-- COLL:drb2 ── ru/kz D-R2: прив'язка до методології без опублікованої версії; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.bindingMethodologyNotPublished', N'ru', N'У методики {methodologyCode} нет опубликованной версии, но она активно привязана к столбцам этой версии шаблона ({bindingCount}): {bindings}. Пересчёт документов отклонялся бы целиком. Опубликуйте методику или отключите эти привязки.'),
    (N'err.ECR-CALC-0422.bindingMethodologyNotPublished', N'kz', N'{methodologyCode} әдістемесінің жарияланған нұсқасы жоқ, бірақ ол осы үлгі нұсқасының бағандарына ({bindingCount}) белсенді байланыстырылған: {bindings}. Құжаттарды қайта есептеу толығымен қабылданбас еді. Әдістемені жариялаңыз немесе осы байланыстыруларды өшіріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:drb2 ── кінець секції ──
-- COLL:drb2 ── D-R2: прив'язка до методології без опублікованої версії; ru/kz — порцією COLL:drb2 нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-0422.bindingMethodologyNotPublished', N'en', N'Methodology {methodologyCode} has no published version, but it is actively bound to {bindingCount} column(s) of this template version: {bindings}. Recalculation of documents would be rejected as a whole. Publish the methodology or deactivate these bindings.', 1),
    (N'err.ECR-TMPL-0422.bindingColumnConflict', N'en', N'Column {columnCode} has several active output bindings with the same row predicate ({outputCode}): each row would have two claimants. Narrow the predicate or deactivate one of the bindings.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:drb2 ── кінець секції ──
