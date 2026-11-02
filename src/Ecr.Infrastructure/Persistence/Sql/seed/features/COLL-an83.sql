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

