-- API:negative-path-2 ── ru/kz відмов замість 500 у нових ендпоінтах; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.patchChangeInvalid', N'ru', N'Каждое изменение патча должно содержать entityType, entityId и field.'),
    (N'err.ECR-TMPL-0422.patchChangeInvalid', N'kz', N'Патчтың әр өзгерісінде entityType, entityId және field болуы керек.'),
    (N'err.ECR-TMPL-0422.presentationValueInvalid', N'ru', N'Значение {entityType}.{field} имеет недопустимую для этого поля форму.'),
    (N'err.ECR-TMPL-0422.presentationValueInvalid', N'kz', N'{entityType}.{field} мәнінің пішіні бұл өріске жарамайды.'),
    (N'err.ECR-REG-0422.definitionItemMissing', N'ru', N'Описание содержит пустой элемент среди полей, правил или ключей.'),
    (N'err.ECR-REG-0422.definitionItemMissing', N'kz', N'Сипаттаманың өрістері, ережелері немесе кілттері арасында бос элемент бар.'),
    (N'err.ECR-REG-0422.switchCodeEmpty', N'ru', N'Набор справочников содержит пустой код.'),
    (N'err.ECR-REG-0422.switchCodeEmpty', N'kz', N'Анықтамалықтар жиынында бос код бар.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- API:negative-path-2 ── кінець секції ──

-- API:negative-path-2 ── відмови замість 500 у нових ендпоінтах (прохід по відмовах 2) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.patchChangeInvalid', N'en', N'Every patch change needs entityType, entityId and field.', 1),
    (N'err.ECR-TMPL-0422.presentationValueInvalid', N'en', N'The value of {entityType}.{field} has a form this field does not accept.', 1),
    (N'err.ECR-REG-0422.definitionItemMissing', N'en', N'The definition contains an empty item among its fields, rules or keys.', 1),
    (N'err.ECR-REG-0422.switchCodeEmpty', N'en', N'The set of registries contains an empty code.', 1)
    -- ru/kz — окремою порцією `API:negative-path-2` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- API:negative-path-2 ── кінець секції ──
