-- COLL:an83 ── ru/kz AN-83 (L5-12, L5-08); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.rules.budgetExhausted', N'ru', N'Правило {rule} не проверено для записи {entryCode}: проверка исчерпала свой бюджет работы. Сохраняйте меньше записей за раз или упростите правило.'),
    (N'err.ECR-REG-4091.registryCodeTakenHidden', N'ru', N'Справочник с кодом «{code}» уже существует: по этому коду на него ссылаются поля выбора из справочника и столбцы шаблонов.'),
    (N'registries.rules.budgetExhausted', N'kz', N'{rule} ережесі {entryCode} жазбасы үшін тексерілмеді: тексеру өзінің жұмыс бюджетін таусты. Бір мезгілде аз жазба сақтаңыз немесе ережені жеңілдетіңіз.'),
    (N'err.ECR-REG-4091.registryCodeTakenHidden', N'kz', N'«{code}» коды бар анықтамалық бұрыннан бар: анықтамалықтан таңдау өрістері мен үлгі бағандары оған осы код арқылы сілтеме жасайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an83 ── кінець секції ──

-- COLL:an83 ── AN-83 (L5-12, L5-08): правило не перевірено — бюджет пакета вичерпано; 409 «код зайнятий» без Id забороненого довідника; ru/kz — порцією COLL:an83 нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.rules.budgetExhausted', N'en', N'Rule {rule} was not checked for entry {entryCode}: the check ran out of its work budget. Save fewer entries at once or simplify the rule.', 1),
    (N'err.ECR-REG-4091.registryCodeTakenHidden', N'en', N'A registry with code "{code}" already exists: the code is what registry-lookup fields and template columns reference it by.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an83 ── кінець секції ──
