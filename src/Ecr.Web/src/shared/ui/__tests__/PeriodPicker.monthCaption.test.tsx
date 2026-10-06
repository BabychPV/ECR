import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import { loadCatalog, setLanguage } from '@/shared/i18n';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { periodCaption } from '@/pages/admin/PeriodsPage';
import { renderWithMantine } from '@/test/render';

/**
 * `A2-10`: підпис періоду під полем «Кезең» і в переліку періодів —
 * казахською з каталогу, а не «September 2026» з `Intl` (у Chrome немає ICU
 * `kk`). Обидва місця йдуть через `formatMonthYear`; тут — що саме через нього.
 */

const Kz = {
  'periods.monthOf': '{month} {year}',
  'periods.month.9': 'Қыркүйек',
  'periods.month.1': 'Қаңтар',
};

beforeAll(async () => {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'kz', revision: 1, strings: Kz }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"public-kz-1"' },
        }),
      ),
    ),
  );
  await loadCatalog('kz', 'public');
  vi.unstubAllGlobals();
  setLanguage('kz');
});

afterAll(() => {
  cleanup();
  setLanguage('en');
  localStorage.clear();
});

describe('підпис місяця — з каталогу мови інтерфейсу', () => {
  it('PeriodPicker: «Қыркүйек 2026» у самому полі, без ключа', () => {
    renderWithMantine(<PeriodPicker value={202609} onChange={vi.fn()} />);

    expect(screen.getByDisplayValue('Қыркүйек 2026')).toBeTruthy();
    expect(screen.queryByDisplayValue('202609')).toBeNull();
  });

  it('PeriodsPage.periodCaption: місячний період — «Қаңтар 2026»', () => {
    expect(periodCaption(2026, 1, 'Monthly')).toBe('Қаңтар 2026');
  });
});
