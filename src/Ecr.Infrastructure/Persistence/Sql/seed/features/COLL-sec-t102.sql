-- COLL:sec-t102 ── ru/kz імені входу (тестувальник T1-03); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-USR-0422.userNameInvalid', N'ru', N'Имя пользователя должно содержать от 1 до {maxLength} символов: только буквы, цифры и . _ - @ \, без пробелов.'),
    (N'err.ECR-USR-0422.userNameInvalid', N'kz', N'Пайдаланушы аты 1-ден {maxLength} таңбаға дейін болуы керек: тек әріптер, сандар және . _ - @ \, бос орынсыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:sec-t102 ── кінець секції ──

-- COLL:sec-t102 ── ім'я входу: порожнє, надто довге або з недозволеними символами (тестувальник T1-03) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-USR-0422.userNameInvalid', N'en', N'The user name must be 1 to {maxLength} characters: letters, digits and . _ - @ \ only, no spaces.', 1)
    -- ru/kz — окремою порцією COLL:sec-t102 у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:sec-t102 ── кінець секції ──
