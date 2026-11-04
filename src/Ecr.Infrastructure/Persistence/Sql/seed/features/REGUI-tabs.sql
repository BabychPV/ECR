-- REG-UI:tabs ── ru/kz UI довідників, коміт 2: пояснення вкладок і сторінки конструктора; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.constructor.description', N'ru', N'Поля, связи, правила и история изменений этого справочника. Изменения попадают в черновик и вступают в силу только после публикации.'),
    (N'registries.constructor.description', N'kz', N'Осы анықтамалықтың өрістері, байланыстары, ережелері және өзгерістер тарихы. Өзгерістер нобайға түседі және тек жарияланғаннан кейін күшіне енеді.'),
    (N'registries.tabHint.relationsEmpty', N'ru', N'Связи выводятся из полей. Чтобы добавить связь, добавьте на вкладке «Поля» поле типа Lookup.'),
    (N'registries.tabHint.relationsEmpty', N'kz', N'Байланыстар өрістерден анықталады. Байланыс қосу үшін «Өрістер» қойындысында Lookup түріндегі өріс қосыңыз.'),
    (N'registries.tabHint.rulesExample', N'ru', N'Пример: правило Expression [Limit] > 0 с серьёзностью Error отклоняет записи, у которых лимит равен нулю или отрицателен; сообщение увидит тот, кто вводит запись.'),
    (N'registries.tabHint.rulesExample', N'kz', N'Мысал: Error маңыздылығы бар Expression ережесі [Limit] > 0 лимиті нөл немесе теріс жазбаларды қабылдамайды; хабарламаны жазбаны енгізуші көреді.'),
    (N'registries.tabHint.mapping', N'ru', N'Только чтение. Показывает, откуда берётся значение каждого поля во внешнем источнике. Сопоставления правятся на экране источника данных.'),
    (N'registries.tabHint.mapping', N'kz', N'Тек оқу. Әр өрістің мәні сыртқы көзден қайдан алынатынын көрсетеді. Сәйкестендірулер деректер көзі экранында өңделеді.'),
    (N'registries.tabHint.history', N'ru', N'Кто, когда и зачем менял описание этого справочника. Причину пишет автор изменения.'),
    (N'registries.tabHint.history', N'kz', N'Осы анықтамалықтың сипаттамасын кім, қашан және неге өзгерткені. Себепті өзгерту авторы жазады.'),
    (N'registries.tabHint.usage', N'ru', N'Шаблоны и колонки, которые зависят от этого справочника. Проверьте это перед изменением описания.'),
    (N'registries.tabHint.usage', N'kz', N'Осы анықтамалыққа тәуелді үлгілер мен бағандар. Сипаттаманы өзгертпес бұрын мұны тексеріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- REG-UI:tabs ── кінець секції ──

-- REG-UI:tabs ── UI довідників, коміт 2: пояснення вкладок і сторінки конструктора; ru/kz — порцією REG-UI:tabs у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.constructor.description', N'en', N'Fields, relations, rules and change history of this registry. Changes go to a draft and apply only when published.', 1),
    (N'registries.tabHint.relationsEmpty', N'en', N'Relations are derived from fields. To add one, add a field of type Lookup on the Fields tab.', 1),
    (N'registries.tabHint.rulesExample', N'en', N'Example: an Expression rule [Limit] > 0 with severity Error refuses entries whose limit is zero or negative; the message is shown to the person entering the entry.', 1),
    (N'registries.tabHint.mapping', N'en', N'Read only. Shows where the value of each field comes from in an external source. Mappings are edited on the data source screen.', 1),
    (N'registries.tabHint.history', N'en', N'Who changed the description of this registry, when and why. The reason is written by the author of the change.', 1),
    (N'registries.tabHint.usage', N'en', N'Templates and columns that depend on this registry. Check this before changing the description.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- REG-UI:tabs ── кінець секції ──
