import { afterAll, describe, expect, it, vi } from 'vitest';
import { loadCatalog, setLanguage } from '@/shared/i18n';
import { lockedRowSentence, monthsText, reopenLabel } from '../LockedRows';

/**
 * B51 (1), F13 RC #849: ru/kz month names are capitalised in the catalog («Январь», «Қаңтар» — they head a period
 * caption), but in running text they are lowercase: «закрытые месяцы (январь и апрель)», «Открыть январь 2026 для
 * этого документа…». English keeps the capital. A month that STARTS the sentence (kz «{period} осы құжат үшін ашу…»)
 * keeps it too.
 *
 * ⛔ Red without the fix: «(Январь и Апрель)», «Открыть Январь 2026 …»; `reopenLabel` did not exist.
 */

const Catalogs: Record<string, Record<string, string>> = {
  ru: {
    'periods.monthOf': '{month} {year}',
    'periods.month.1': 'Январь',
    'periods.month.4': 'Апрель',
    'periods.month.7': 'Июль',
    'inspector.attention.monthList': '{list} и {last}',
    'inspector.attention.lockedRowMany': 'Строка {row}: закрытые месяцы ({months}) — эти столбцы здесь не редактируются.',
    'document.reopen.action': 'Открыть {period} для этого документа…',
  },
  kz: {
    'periods.monthOf': '{month} {year}',
    'periods.month.1': 'Қаңтар',
    'periods.month.4': 'Сәуір',
    'inspector.attention.monthList': '{list} және {last}',
    'document.reopen.action': '{period} осы құжат үшін ашу…',
  },
  en: {
    'periods.monthOf': '{month} {year}',
    'periods.month.1': 'January',
    'periods.month.4': 'April',
    'inspector.attention.monthList': '{list} and {last}',
    'document.reopen.action': 'Reopen {period} for this document…',
  },
};

async function useLanguage(lang: string): Promise<void> {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: lang, revision: 1, strings: Catalogs[lang] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: `"public-${lang}-1"` },
        }),
      ),
    ),
  );
  await loadCatalog(lang, 'public');
  vi.unstubAllGlobals();
  setLanguage(lang);
}

afterAll(() => {
  setLanguage('en');
  localStorage.clear();
});

describe('B51 (1): month names mid-sentence', () => {
  it('ru: lowercase in the month list and in «Открыть … для этого документа…»', async () => {
    await useLanguage('ru');

    expect(monthsText([1, 4])).toBe('январь и апрель');
    expect(
      lockedRowSentence({
        key: '1:R1:locked',
        tableInstanceId: 1,
        periodKey: 202610,
        rowKey: 'R1',
        rowName: null,
        months: [1, 4, 7],
        periodKeys: [202601, 202604, 202607],
        columns: '',
        columnCode: 'C1',
      }),
    ).toBe('Строка R1: закрытые месяцы (январь, апрель и июль) — эти столбцы здесь не редактируются.');
    expect(reopenLabel(202601)).toBe('Открыть январь 2026 для этого документа…');
  });

  it('kz: lowercase in the list; the month that starts the sentence keeps its capital', async () => {
    await useLanguage('kz');

    expect(monthsText([1, 4])).toBe('қаңтар және сәуір');
    expect(reopenLabel(202601)).toBe('Қаңтар 2026 осы құжат үшін ашу…');
  });

  it('en: capitalised as before', async () => {
    await useLanguage('en');

    expect(monthsText([1, 4])).toBe('January and April');
    expect(reopenLabel(202601)).toBe('Reopen January 2026 for this document…');
  });
});
