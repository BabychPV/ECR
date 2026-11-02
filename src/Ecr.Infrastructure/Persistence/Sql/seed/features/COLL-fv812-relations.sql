-- COLL:fv812-relations ── ru/kz редагування зв'язків полів у конструкторі довідників (ФВ-8.12, порція 1); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.lookupRetargetInUse', N'ru', N'Связь поля «{fieldCode}» нельзя изменить: в записях уже есть значения, указывающие на текущий справочник. Сначала очистите или измените эти значения, затем меняйте связь.'),
    (N'err.ECR-REG-0422.lookupRetargetInUse', N'kz', N'«{fieldCode}» өрісінің байланысын өзгерту мүмкін емес: жазбаларда ағымдағы анықтамалыққа сілтейтін мәндер бар. Алдымен осы мәндерді тазалаңыз немесе өзгертіңіз, содан кейін байланысты өзгертіңіз.'),
    (N'err.ECR-REG-0422.lookupTargetUnknown', N'ru', N'Справочника, на который указывает поле «{fieldCode}», не существует.'),
    (N'err.ECR-REG-0422.lookupTargetUnknown', N'kz', N'«{fieldCode}» өрісі сілтейтін анықтамалық жоқ.'),
    (N'registries.relationTargetFor', N'ru', N'Цель связи поля {field}'),
    (N'registries.relationTargetFor', N'kz', N'{field} өрісінің байланыс мақсаты'),
    (N'registries.relationsEditHint', N'ru', N'Цель связи можно заменить другим справочником, пока ни в одной записи нет значения этого поля; иначе сервер откажет.'),
    (N'registries.relationsEditHint', N'kz', N'Байланыс мақсатын осы өрістің мәні бірде-бір жазбада болмаған кезде басқа анықтамалыққа ауыстыруға болады; әйтпесе сервер бас тартады.'),
    (N'err.ECR-REG-0422.lookupRetargetUsedByRules', N'ru', N'Связь поля «{fieldCode}» нельзя изменить: через неё читают атрибуты правил, формул или версий методологий — {total}: {usedBy}. Сначала измените или отключите их.'),
    (N'err.ECR-REG-0422.lookupRetargetUsedByRules', N'kz', N'«{fieldCode}» өрісінің байланысын өзгерту мүмкін емес: ол арқылы атрибуттарды оқитын ережелер, формулалар немесе әдістеме нұсқалары бар — {total}: {usedBy}. Алдымен оларды өзгертіңіз немесе өшіріңіз.'),
    (N'err.ECR-REG-0409.definitionChanged', N'ru', N'Определение справочника «{registryCode}» было изменено после того, как вы его открыли. Перезагрузите его и повторите свои изменения.'),
    (N'err.ECR-REG-0409.definitionChanged', N'kz', N'«{registryCode}» анықтамалығының анықтамасы Сіз оны ашқаннан кейін өзгертілген. Оны қайта жүктеп, өзгерістеріңізді қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:fv812-relations ── кінець секції ──

-- COLL:fv812-relations ── редагування зв'язків полів у конструкторі довідників (ФВ-8.12, порція 1) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0422.lookupRetargetInUse', N'en', N'The link of field "{fieldCode}" cannot be changed: records already hold values pointing to the current target. Clear or change those values first, then change the link.', 1),
    (N'err.ECR-REG-0422.lookupTargetUnknown', N'en', N'The target registry of field "{fieldCode}" does not exist.', 1),
    (N'registries.relationTargetFor', N'en', N'Link target of field {field}', 1),
    (N'registries.relationsEditHint', N'en', N'The target of a link can be changed to another registry while no record holds a value in that field; otherwise the server refuses it.', 1),
    (N'err.ECR-REG-0422.lookupRetargetUsedByRules', N'en', N'The link of field "{fieldCode}" cannot be changed: {total} rule(s), formula(s) or methodology version(s) read attributes through it: {usedBy}. Change or disable them first.', 1),
    (N'err.ECR-REG-0409.definitionChanged', N'en', N'The definition of registry "{registryCode}" was changed after you opened it. Reload it and repeat your changes.', 1)
    -- ru/kz — окремою порцією COLL:fv812-relations у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:fv812-relations ── кінець секції ──
