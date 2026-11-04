-- COLL:l104 ── ru/kz L1-04: небезпечний запит з чужого сайту; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-AUTH-0403.csrfOrigin', N'ru', N'Запрос пришёл с другого сайта и отклонён. Откройте приложение по его собственному адресу и повторите действие.'),
    (N'err.ECR-AUTH-0403.csrfOrigin', N'kz', N'Сұрау басқа сайттан келді және қабылданбады. Қолданбаны өз мекенжайы бойынша ашып, әрекетті қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:l104 ── кінець секції ──
-- COLL:l104 ── L1-04: небезпечний запит з чужого сайту (CSRF, Origin / Sec-Fetch-Site); ru/kz — порцією COLL:l104 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-AUTH-0403.csrfOrigin', N'en', N'This request came from another website and was rejected. Open the application at its own address and repeat the action.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:l104 ── кінець секції ──
