import type { MappingOutcome } from '@/api/types';
import { t } from '@/shared/i18n';

/**
 * Підпис стану мапінгу.
 *
 * ⛔ `switch` із явними ключами, а не `t(\`mapping.outcome.${outcome}\`)`.
 * Зібраний ключ невидимий для сторожа «кожен рядок, якого просить клієнт, є в
 * каталозі» — тобто новий різновид розриву мовчки з'явився б на екрані як
 * `⟦mapping.…⟧`. Тут же він не збереться взагалі: TypeScript вимагає розібрати
 * кожен випадок переліку.
 */
export function outcomeLabel(outcome: MappingOutcome): string {
  switch (outcome) {
    case 'Materialized':
      return t('mapping.materialized');
    case 'RawOnly':
      return t('mapping.rawOnly');
    case 'Unmapped':
      return t('mapping.unmappedRow');
    case 'TargetMissing':
      return t('mapping.targetMissing');
    case 'NoData':
      return t('mapping.noData');
    default:
      return outcome;
  }
}

/**
 * Колір стану.
 *
 * ⚠ `RawOnly` — сірий, а не жовтий: це свідомий вибір людини (`D-118`), і
 * попереджати про нього означало б привчити не читати попередження.
 */
export function outcomeColor(outcome: MappingOutcome): string {
  switch (outcome) {
    case 'Materialized':
      return 'green';
    case 'RawOnly':
      return 'gray';
    default:
      return 'red';
  }
}
