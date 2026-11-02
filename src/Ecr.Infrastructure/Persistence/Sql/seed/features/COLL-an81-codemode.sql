-- COLL:an81-codemode ── ru/kz: AN-81 / L4-13: порада до події синку «елемент не створено: режим кодів Manual»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.codeModeManualNoAutoCreate', N'ru', N'Элемент не создан как запись справочника: у этого справочника ручные коды записей, а синхронизация создаёт записи из элементов источника только в режиме кодов Auto. Используйте справочник с режимом кодов Auto (после появления записей режим изменить нельзя) или добавьте запись вручную.'),
    (N'err.ECR-REG-0422.codeModeManualNoAutoCreate', N'kz', N'Элемент анықтамалық жазбасы ретінде жасалмады: бұл анықтамалықтың жазба кодтары қолмен енгізіледі, ал синхрондау жазбаларды көз элементтерінен тек Auto код режимінде жасайды. Auto код режимі бар анықтамалықты пайдаланыңыз (жазбалар пайда болғаннан кейін режимді өзгертуге болмайды) немесе жазбаны қолмен қосыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an81-codemode ── кінець секції ──

-- COLL:an81-codemode ── AN-81 / L4-13: порада до події синку «елемент не створено: режим кодів Manual»; ru/kz — порцією COLL:an81-codemode нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0422.codeModeManualNoAutoCreate', N'en', N'The element was not created as a registry entry: this registry uses Manual entry codes, and sync creates entries from source elements only in Auto code mode. Use a registry with the Auto code mode (the mode cannot be changed once the registry has entries), or add the entry by hand.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an81-codemode ── кінець секції ──
