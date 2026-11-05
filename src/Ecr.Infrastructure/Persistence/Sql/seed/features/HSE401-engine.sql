-- HSE401:engine ── ECR-TMPL-0422 ── ru/kz: фільтр довідника колонки (level=<код рівня>); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.lookupFilterUnsupported', N'ru', N'Фильтр справочника колонки «{columnCode}» должен иметь вид level=<код уровня>; получено «{filter}».'),
    (N'err.ECR-TMPL-0422.lookupFilterUnsupported', N'kz', N'«{columnCode}» бағанының анықтамалық сүзгісі level=<деңгей коды> түрінде болуы керек; алынған мән «{filter}».')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- HSE401:engine ── кінець секції ──
-- HSE401:engine ── ECR-TMPL-0422 ── en: фільтр довідника колонки; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.lookupFilterUnsupported', N'en', N'The lookup filter of column "{columnCode}" must have the form level=<level code>; got "{filter}".', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE401:engine ── кінець секції ──
