import { describe, expect, it, vi } from 'vitest';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { warningKind } from '../bindingModel';
import { warningText } from '../bindingText';

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => key,
}));

/**
 * CL-30 (F13 PM-10b): кожне попередження прив'язки показується текстом КАТАЛОГУ, а не сирим текстом сервера.
 * `binding.warning.periodClosed` (`PermitBindingService`) не мав ні випадку на клієнті, ні ключа в сіді.
 * Каталог тестів читає сід (`a11yFixtures.Catalog`), тож ключ без рядка сіду тут червоний. Повноту «кожен випадок -
 * ключ у en/ru/kz» тримає сторож `seedKeys.test.ts`.
 */

describe('тексти попереджень прив’язки (CL-30)', () => {
  it('«період закрито» сервера - свій випадок і свій текст каталогу', () => {
    const kind = warningKind({ code: 'binding.warning.periodClosed', text: 'Період закрито: рядки з дозволу не створено.' });
    expect(kind).toBe('periodClosed');
    expect(warningText(kind)).toBe('permits.sheetBinding.warning.periodClosed');
    expect(Catalog['permits.sheetBinding.warning.periodClosed']).toBe('The period is closed, so no rows were created from the permit.');
  });
});
