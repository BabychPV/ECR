import { t } from '@/shared/i18n';

/**
 * Правила нічної перевірки узгодженості в порядку показу — той самий перелік, що `ConsistencyCheckJob.KnownRules`
 * на сервері (сторож `ConsistencyRuleCatalogTests` звіряє обидва й ключі каталогу `consistency.rule.<CODE>`).
 *
 * ⚠ Аудит 24.11: фільтр «Rule» був вільним текстом, і з нулем знахідок людина не могла дізнатися, які правила взагалі
 * існують. Тепер — перелік відомих кодів із людськими назвами.
 */
export const ConsistencyRuleCodes = [
  'ORPHANED_CELL',
  'BROKEN_FK',
  'ARCHIVE_CHECKSUM',
  'UNBOUND_CALCULATED_COLUMN',
  'UNSOURCED_FORMULA_COLUMN',
  'NON_CURRENT_RUN_ROWS',
  'STALE_APPROVED_DEPENDENT',
  'CALC_RESULT_VERSION_NOT_EFFECTIVE',
  'EFFECTIVE_VERSION_FORMULAS',
  'ROW_INPUTS_COMPLETE_NO_RESULT',
  'CURRENT_RUN_NO_RESULTS_WITH_BINDINGS',
  'CALC_ISSUE_WITHOUT_RUN',
  'OUTPUT_WITHOUT_BINDING',
  'BINDING_DEAD_COLUMN',
] as const;

/** Код правила, відомий клієнтові. */
export type ConsistencyRuleCode = (typeof ConsistencyRuleCodes)[number];

/** Чи відомий код правила (код з журналу — дані сервера, може бути й старим). */
export function isKnownRule(code: string): code is ConsistencyRuleCode {
  return (ConsistencyRuleCodes as readonly string[]).includes(code);
}

/**
 * Людська назва правила мовою інтерфейсу; невідомий код — як є.
 *
 * ⚠ Ключі — ЛІТЕРАЛАМИ: сторожі каталогу шукають саме виклик `t('…')`, складеного ключа вони не побачили б.
 */
export function ruleLabel(code: string): string {
  switch (code) {
    case 'ORPHANED_CELL':
      return t('consistency.rule.ORPHANED_CELL');
    case 'BROKEN_FK':
      return t('consistency.rule.BROKEN_FK');
    case 'ARCHIVE_CHECKSUM':
      return t('consistency.rule.ARCHIVE_CHECKSUM');
    case 'UNBOUND_CALCULATED_COLUMN':
      return t('consistency.rule.UNBOUND_CALCULATED_COLUMN');
    case 'UNSOURCED_FORMULA_COLUMN':
      return t('consistency.rule.UNSOURCED_FORMULA_COLUMN');
    case 'NON_CURRENT_RUN_ROWS':
      return t('consistency.rule.NON_CURRENT_RUN_ROWS');
    case 'STALE_APPROVED_DEPENDENT':
      return t('consistency.rule.STALE_APPROVED_DEPENDENT');
    case 'CALC_RESULT_VERSION_NOT_EFFECTIVE':
      return t('consistency.rule.CALC_RESULT_VERSION_NOT_EFFECTIVE');
    case 'EFFECTIVE_VERSION_FORMULAS':
      return t('consistency.rule.EFFECTIVE_VERSION_FORMULAS');
    case 'ROW_INPUTS_COMPLETE_NO_RESULT':
      return t('consistency.rule.ROW_INPUTS_COMPLETE_NO_RESULT');
    case 'CURRENT_RUN_NO_RESULTS_WITH_BINDINGS':
      return t('consistency.rule.CURRENT_RUN_NO_RESULTS_WITH_BINDINGS');
    case 'CALC_ISSUE_WITHOUT_RUN':
      return t('consistency.rule.CALC_ISSUE_WITHOUT_RUN');
    case 'OUTPUT_WITHOUT_BINDING':
      return t('consistency.rule.OUTPUT_WITHOUT_BINDING');
    case 'BINDING_DEAD_COLUMN':
      return t('consistency.rule.BINDING_DEAD_COLUMN');
    default:
      return code;
  }
}
