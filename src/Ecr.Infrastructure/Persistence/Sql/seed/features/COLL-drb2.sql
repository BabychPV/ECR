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
