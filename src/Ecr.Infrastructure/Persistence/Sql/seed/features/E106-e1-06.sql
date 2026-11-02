-- E1-06 ── ru/kz: тіло запиту завелике; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.requestTooLarge', N'ru', N'Тело запроса больше, чем принимает сервер. Отправляйте меньше данных за один запрос.'),
    (N'err.ECR-REQ-0422.requestTooLarge', N'kz', N'Сұраныс денесі сервер қабылдайтын көлемнен үлкен. Бір сұраныста азырақ деректер жіберіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- E1-06 ── кінець секції ──

-- E1-06 ── Тіло запиту понад межу — 413 замість 500 (`ExceptionHandlingMiddleware`, `BadHttpRequestException`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.requestTooLarge', N'en', N'The request body is larger than the server accepts. Send less data in one request.', 1)
    -- ru/kz — окремою порцією `E1-06` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- E1-06 ── кінець секції ──
