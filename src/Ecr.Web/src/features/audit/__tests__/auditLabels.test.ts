import { describe, expect, it } from 'vitest';
import { entityTypeLabel, humanizeIdentifier, operationLabel, securityEventLabel } from '../auditLabels';

/**
 * RC19: операція, сутність і подія безпеки в журналі Audit — не сирі ідентифікатори.
 *
 * ⛔ Каталог у клієнтських тестах не завантажується, тож `t()` дає позначений ключ
 * `⟦ключ⟧` лише для ключів, що в каталозі Є; для відсутніх `hasText` хибний — і підпис мусить бути
 * читабельною формою ідентифікатора, а не сирим рядком із сервера.
 */
describe('humanizeIdentifier', () => {
  it('розбиває PascalCase на слова, лишаючи перше з великої', () => {
    expect(humanizeIdentifier('SaveCollectionSchedule')).toBe('Save collection schedule');
  });

  it('прибирає схему БД і крапки', () => {
    expect(humanizeIdentifier('cfg.TableDef')).toBe('Table def');
    expect(humanizeIdentifier('Project.CurrentPeriod')).toBe('Project current period');
  });

  it('порожній результат — повертає вхід', () => {
    expect(humanizeIdentifier('')).toBe('');
  });
});

describe('підписи журналу Audit: невідоме значення', () => {
  it('операція, якої немає в каталозі, — читабельна, а не сира', () => {
    expect(operationLabel('CreateSourceEventMap')).toBe('Create source event map');
  });

  it('сутність без ключа — без схеми й у словах', () => {
    expect(entityTypeLabel('cfg.SomethingNew')).toBe('Something new');
  });

  it('подія безпеки без ключа — у словах', () => {
    expect(securityEventLabel('JobRestarted')).toBe('Job restarted');
  });
});
