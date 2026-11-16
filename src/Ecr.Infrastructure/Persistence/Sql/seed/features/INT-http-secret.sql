-- INT:http-secret ── ru/kz: PT-5 (пентест 2026-11): секрет джерела PI Web API не йде по http://; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-INT-0502.piWebApiInsecureHttp', N'ru', N'Источник PI Web API с секретом в заголовке (Basic или Bearer) настроен на адрес http://: секрет не отправляется открытым текстом. Укажите адрес https:// или разрешите http явно (PiWebApi:AllowInsecureHttp).'),
    (N'err.ECR-INT-0502.piWebApiInsecureHttp', N'kz', N'Тақырыпта құпиясы бар (Basic немесе Bearer) PI Web API көзі http:// мекенжайына бапталған: құпия ашық мәтінмен жіберілмейді. https:// мекенжайын көрсетіңіз немесе http-ге анық рұқсат беріңіз (PiWebApi:AllowInsecureHttp).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- INT:http-secret ── кінець секції ──
-- INT:http-secret ── PT-5: відмова збирача слати секрет по http://; ru/kz — порцією INT:http-secret вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-INT-0502.piWebApiInsecureHttp', N'en', N'The PI Web API source with a secret in the header (Basic or Bearer) is set to an http:// address: the secret is not sent in clear text. Use an https:// address, or allow http explicitly (PiWebApi:AllowInsecureHttp).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- INT:http-secret ── кінець секції ──
-- INT:http-secret ── змінений текст наявного ключа (PT-5): схема адреси — для секрету в заголовку лише https ──
-- Нове значення вже стоїть у MERGE (COLL-ssrf.sql); епілог оновлює, лише поки в базі старе.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'err.ECR-REQ-0422.dataSourceEndpointScheme', N'en', N'The data source address must use http or https.', N'The data source address must use http or https. A source with a secret in the header (Basic or Bearer) accepts https only.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointScheme', N'ru', N'Адрес источника данных должен использовать http или https.', N'Адрес источника данных должен использовать http или https. Для источника с секретом в заголовке (Basic или Bearer) допускается только https.'),
    (N'err.ECR-REQ-0422.dataSourceEndpointScheme', N'kz', N'Деректер көзінің мекенжайы http немесе https пайдалануы тиіс.', N'Деректер көзінің мекенжайы http немесе https пайдалануы тиіс. Тақырыпта құпиясы бар (Basic немесе Bearer) көз үшін тек https рұқсат етіледі.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- INT:http-secret ── кінець секції ──
