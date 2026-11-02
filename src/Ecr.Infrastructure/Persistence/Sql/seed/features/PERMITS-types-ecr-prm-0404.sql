-- PERMITS: types ── ECR-PRM-0404 ── ru/kz: тип дозволу не знайдено; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0404', N'ru', N'Тип разрешения не найден'),
    (N'err.ECR-PRM-0404', N'kz', N'Рұқсат түрі табылмады'),
    (N'err.ECR-PRM-0404.type', N'ru', N'Типа разрешения «{code}» не существует.'),
    (N'err.ECR-PRM-0404.type', N'kz', N'«{code}» рұқсат түрі жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: types ── ECR-PRM-0404 ── кінець секції ──

-- PERMITS: types ── en: відмови типів дозволів (ECR-PRM-*, PM-03) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-PRM-0404', N'en', N'Permit type not found', 1),
    (N'err.ECR-PRM-0404.type', N'en', N'Permit type "{code}" does not exist.', 1),
    (N'err.ECR-PRM-0422', N'en', N'Permit type definition is invalid', 1),
    (N'err.ECR-PRM-0422.nameRequired', N'en', N'The name of "{item}" is required in at least one language.', 1),
    (N'err.ECR-PRM-0422.categoryUnknown', N'en', N'Category "{category}" does not exist. Use Air, Water, Waste or Other.', 1),
    (N'err.ECR-PRM-0422.tableCodeInvalid', N'en', N'Table code "{tableCode}" is not allowed: use Latin letters, digits and underscore, starting with a letter.', 1),
    (N'err.ECR-PRM-0422.tableDuplicate', N'en', N'Table "{tableCode}" is listed twice.', 1),
    (N'err.ECR-PRM-0422.registryCodeInvalid', N'en', N'The internal code "{registryCode}" is too long or not allowed. Shorten the type, table or column code.', 1),
    (N'err.ECR-PRM-0422.columnCodeInvalid', N'en', N'Column code "{columnCode}" in table "{tableCode}" is not allowed: use Latin letters, digits and underscore, starting with a letter.', 1),
    (N'err.ECR-PRM-0422.columnCodeReserved', N'en', N'Column code "{columnCode}" in table "{tableCode}" is reserved for the link to the permit.', 1),
    (N'err.ECR-PRM-0422.columnDuplicate', N'en', N'Column "{columnCode}" is listed twice in table "{tableCode}".', 1),
    (N'err.ECR-PRM-0422.columnTypeUnknown', N'en', N'Column "{columnCode}" has an unsupported type "{dataType}".', 1),
    (N'err.ECR-PRM-0422.tableWithoutKey', N'en', N'Table "{tableCode}" has no key column: its rows would have no address. Mark at least one column as the key.', 1),
    (N'err.ECR-PRM-0422.numericFormat', N'en', N'Precision and scale of column "{columnCode}" are not valid: only Decimal columns have them, precision is 1 to 38, scale is not larger than precision.', 1),
    (N'err.ECR-PRM-0422.listInconsistent', N'en', N'The list of column "{columnCode}" is not consistent with its source: a shared list needs only a registry code, a table list only a sibling table.', 1),
    (N'err.ECR-PRM-0422.listForNonLookup', N'en', N'Column "{columnCode}" is not a Lookup but has list settings.', 1),
    (N'err.ECR-PRM-0422.typeOnlyNoChoices', N'en', N'Column "{columnCode}" takes values from a list for this type only, but the list has no values.', 1),
    (N'err.ECR-PRM-0422.choicesInvalid', N'en', N'The values of column "{columnCode}" contain an empty or repeated value.', 1),
    (N'err.ECR-PRM-0422.siblingUnknown', N'en', N'Column "{columnCode}" refers to the table "{siblingTableCode}", which is not another table of this type.', 1),
    (N'err.ECR-PRM-0422.siblingCycle', N'en', N'Tables refer to each other in a circle: {chain}.', 1),
    (N'err.ECR-PRM-0422.ruleCodeInvalid', N'en', N'Check code "{ruleCode}" is not allowed.', 1),
    (N'err.ECR-PRM-0422.ruleDuplicate', N'en', N'Check "{ruleCode}" is listed twice.', 1),
    (N'err.ECR-PRM-0422.ruleExpressionRequired', N'en', N'The expression of check "{ruleCode}" is empty.', 1),
    (N'err.ECR-PRM-0422.ruleSeverityUnknown', N'en', N'Level "{severity}" of check "{ruleCode}" does not exist. Use Warn or Block.', 1),
    (N'err.ECR-PRM-0422.tableRemoved', N'en', N'Table "{tableCode}" is published and cannot be removed.', 1),
    (N'err.ECR-PRM-0422.columnRemoved', N'en', N'Column "{columnCode}" of table "{tableCode}" is published and cannot be removed: rows refer to its values.', 1),
    (N'err.ECR-PRM-0422.columnRetyped', N'en', N'The type of published column "{columnCode}" of table "{tableCode}" cannot be changed.', 1),
    (N'err.ECR-PRM-0422.keyChanged', N'en', N'The key of published table "{tableCode}" cannot be changed.', 1),
    (N'err.ECR-PRM-0422.noTables', N'en', N'A permit type without tables cannot be published.', 1),
    (N'err.ECR-PRM-0422.reasonTooShort', N'en', N'The reason for publishing must have at least {min} characters.', 1),
    (N'err.ECR-PRM-0422.groupCodeInvalid', N'en', N'Group code "{group}" of column "{columnCode}" is not allowed: use Latin letters, digits and underscore, starting with a letter.', 1),
    (N'err.ECR-PRM-0422.sharedRegistryUnknown', N'en', N'Column "{columnCode}" refers to the shared registry "{registryCode}", which does not exist.', 1),
    (N'err.ECR-PRM-4091', N'en', N'Permit type code is taken', 1),
    (N'err.ECR-PRM-4091.code', N'en', N'A permit type with code "{code}" already exists.', 1),
    (N'err.ECR-PRM-4091.registryCode', N'en', N'The registry code "{registryCode}" needed for the permit type is already taken.', 1),
    (N'err.ECR-PRM-4093', N'en', N'Permit type was changed', 1),
    (N'err.ECR-PRM-4093.changed', N'en', N'The permit type "{code}" was changed or published after you opened it. Reload it and repeat your edit.', 1),
    (N'err.ECR-PRM-4093.noDraft', N'en', N'The permit type "{code}" is published and has no open draft. Open a new version first.', 1),
    (N'err.ECR-PRM-4093.draftExists', N'en', N'The permit type "{code}" already has an open draft of a new version.', 1),
    (N'err.ECR-PRM-4093.notPublished', N'en', N'The permit type "{code}" has not been published yet, so there is no version to start from.', 1),
    (N'err.ECR-PRM-4094', N'en', N'Permit type is in use', 1),
    (N'err.ECR-PRM-4094.published', N'en', N'The permit type "{code}" is published and cannot be deleted.', 1),
    (N'err.ECR-PRM-4094.hasPermits', N'en', N'The permit type "{code}" already has {count} permits and cannot be deleted.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: types ── кінець секції ──
