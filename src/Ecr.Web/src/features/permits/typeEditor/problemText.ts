import { t } from '@/shared/i18n';
import type { DraftProblem } from './draftValidate';

/**
 * Текст вади копії мовою користувача (Permits, PM-08b). Ключі — ті самі `err.ECR-PRM-0422.*`, що й у
 * відмові сервера, тож речення одне й те саме до і після `PUT`.
 *
 * ⛔ Кожен ключ тут — ЛІТЕРАЛ: сторож `EndpointCoverageTests` не пускає `t(змінна)` і перевіряє кожен
 * літерал проти сіду. Тому не `t(problem.messageKey)`, а таблиця. Новий випадок у `draftValidate.ts` —
 * новий рядок тут (тест `problemText.test.ts` читає validator і падає, якщо рядка немає).
 */
export function problemText(problem: DraftProblem): string {
  const p = problem.params;
  switch (problem.messageKey) {
    case 'err.ECR-PRM-0422.nameRequired':
      return t('err.ECR-PRM-0422.nameRequired', p);
    case 'err.ECR-PRM-0422.categoryUnknown':
      return t('err.ECR-PRM-0422.categoryUnknown', p);
    case 'err.ECR-PRM-0422.registryCodeInvalid':
      return t('err.ECR-PRM-0422.registryCodeInvalid', p);
    case 'err.ECR-PRM-0422.tableCodeInvalid':
      return t('err.ECR-PRM-0422.tableCodeInvalid', p);
    case 'err.ECR-PRM-0422.tableDuplicate':
      return t('err.ECR-PRM-0422.tableDuplicate', p);
    case 'err.ECR-PRM-0422.columnCodeInvalid':
      return t('err.ECR-PRM-0422.columnCodeInvalid', p);
    case 'err.ECR-PRM-0422.columnCodeReserved':
      return t('err.ECR-PRM-0422.columnCodeReserved', p);
    case 'err.ECR-PRM-0422.columnDuplicate':
      return t('err.ECR-PRM-0422.columnDuplicate', p);
    case 'err.ECR-PRM-0422.columnTypeUnknown':
      return t('err.ECR-PRM-0422.columnTypeUnknown', p);
    case 'err.ECR-PRM-0422.groupCodeInvalid':
      return t('err.ECR-PRM-0422.groupCodeInvalid', p);
    case 'err.ECR-PRM-0422.numericFormat':
      return t('err.ECR-PRM-0422.numericFormat', p);
    case 'err.ECR-PRM-0422.listForNonLookup':
      return t('err.ECR-PRM-0422.listForNonLookup', p);
    case 'err.ECR-PRM-0422.listInconsistent':
      return t('err.ECR-PRM-0422.listInconsistent', p);
    case 'err.ECR-PRM-0422.typeOnlyNoChoices':
      return t('err.ECR-PRM-0422.typeOnlyNoChoices', p);
    case 'err.ECR-PRM-0422.choicesInvalid':
      return t('err.ECR-PRM-0422.choicesInvalid', p);
    case 'err.ECR-PRM-0422.siblingUnknown':
      return t('err.ECR-PRM-0422.siblingUnknown', p);
    case 'err.ECR-PRM-0422.siblingCycle':
      return t('err.ECR-PRM-0422.siblingCycle', p);
    case 'err.ECR-PRM-0422.tableWithoutKey':
      return t('err.ECR-PRM-0422.tableWithoutKey', p);
    case 'err.ECR-PRM-0422.ruleCodeInvalid':
      return t('err.ECR-PRM-0422.ruleCodeInvalid', p);
    case 'err.ECR-PRM-0422.ruleDuplicate':
      return t('err.ECR-PRM-0422.ruleDuplicate', p);
    case 'err.ECR-PRM-0422.ruleExpressionRequired':
      return t('err.ECR-PRM-0422.ruleExpressionRequired', p);
    case 'err.ECR-PRM-0422.ruleSeverityUnknown':
      return t('err.ECR-PRM-0422.ruleSeverityUnknown', p);
    case 'err.ECR-PRM-0422.tableRemoved':
      return t('err.ECR-PRM-0422.tableRemoved', p);
    case 'err.ECR-PRM-0422.columnRemoved':
      return t('err.ECR-PRM-0422.columnRemoved', p);
    case 'err.ECR-PRM-0422.columnRetyped':
      return t('err.ECR-PRM-0422.columnRetyped', p);
    case 'err.ECR-PRM-0422.keyChanged':
      return t('err.ECR-PRM-0422.keyChanged', p);
    case 'err.ECR-PRM-0422.noTables':
      return t('err.ECR-PRM-0422.noTables', p);
    default:
      // Невідомий випадок показуємо ключем: порожній рядок нічого б не пояснив.
      return problem.messageKey;
  }
}
