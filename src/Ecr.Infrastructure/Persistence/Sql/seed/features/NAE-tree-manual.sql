-- NAE:tree-manual ── ru/kz структура НАЕ без AF: корінь, корені з LAND_REGION, імпорт з Excel; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0422.naeRegionRequired', N'ru', N'Нужен код региона.'),
    (N'err.ECR-REG-0422.naeTreeNotEmpty', N'ru', N'В дереве НАЕ уже {count} узлов: корни создаются только в пустом дереве.'),
    (N'err.ECR-REG-0422.naeSeedSourceMissing', N'ru', N'В справочнике «{registryCode}» нет регионов для создания корней.'),
    (N'err.ECR-IMP-0422.naeImportHasErrors', N'ru', N'В файле ошибок: {count}. Ничего не импортировано.'),
    (N'registryAudit.naeExcelImport', N'ru', N'Создано узлов НАЕ из Excel: {count}.'),
    (N'registryAudit.naeRootCreated', N'ru', N'Создано корневых узлов НАЕ: {count}.'),
    (N'err.ECR-REG-0422.naeRegionRequired', N'kz', N'Аймақ коды қажет.'),
    (N'err.ECR-REG-0422.naeTreeNotEmpty', N'kz', N'НАЕ ағашында {count} түйін бар: түбірлер тек бос ағашта жасалады.'),
    (N'err.ECR-REG-0422.naeSeedSourceMissing', N'kz', N'«{registryCode}» анықтамалығында түбір жасауға аймақтар жоқ.'),
    (N'err.ECR-IMP-0422.naeImportHasErrors', N'kz', N'Файлдағы қателер: {count}. Ештеңе импортталмады.'),
    (N'registryAudit.naeExcelImport', N'kz', N'Excel-ден жасалған НАЕ түйіндері: {count}.'),
    (N'registryAudit.naeRootCreated', N'kz', N'Жасалған түбір НАЕ түйіндері: {count}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- NAE:tree-manual ── кінець секції ──
-- NAE:tree-manual ── en: структура НАЕ без AF (помилки й журнал); ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0422.naeRegionRequired', N'en', N'A region code is required.', 1),
    (N'err.ECR-REG-0422.naeTreeNotEmpty', N'en', N'The NAE tree already has {count} nodes: roots are seeded only into an empty tree.', 1),
    (N'err.ECR-REG-0422.naeSeedSourceMissing', N'en', N'Registry «{registryCode}» has no regions to seed from.', 1),
    (N'err.ECR-IMP-0422.naeImportHasErrors', N'en', N'The file has {count} error(s). Nothing was imported.', 1),
    (N'registryAudit.naeExcelImport', N'en', N'NAE nodes created from Excel: {count}.', 1),
    (N'registryAudit.naeRootCreated', N'en', N'NAE root nodes created: {count}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- NAE:tree-manual ── кінець секції ──
