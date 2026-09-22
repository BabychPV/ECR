import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  defaultRuleCoverageWindow,
  ruleCoverage,
  ruleCoverageCell,
  ruleCoverageOtherRules,
  ruleCoverageWindowEmpty,
} from '../ruleCoverage';

/**
 * Звернення й чисті функції матриці покриття (`ФВ-13.9`).
 *
 * ⚠ Адреса перевіряється разом із МЕТОДОМ і параметрами: сторож
 * `Кожна_дія_сервера_має_споживача_в_інтерфейсі` бачить лише літерал у коді, а
 * чи доїхали `tableDefId`/`periodFrom`/`periodTo` — не бачить ніхто, крім цього
 * тесту.
 */

function mockFetch(): { sent: { url: string; method: string }[] } {
  const sent: { url: string; method: string }[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      sent.push({ url: String(input), method: init?.method ?? 'GET' });

      return new Response(JSON.stringify({ combinations: [] }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );

  return { sent };
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ruleCoverage: звернення', () => {
  it('шле GET на rule-coverage з вікном і таблицею', async () => {
    const { sent } = mockFetch();

    await ruleCoverage(3, 42, { tableDefId: 7, periodFrom: 202601, periodTo: 202612 });

    expect(sent).toHaveLength(1);
    expect(sent[0]?.method).toBe('GET');
    expect(sent[0]?.url).toBe(
      '/api/v1/methodologies/3/versions/42/rule-coverage?tableDefId=7&periodFrom=202601&periodTo=202612',
    );
  });

  it('без обраної таблиці параметра немає зовсім', async () => {
    const { sent } = mockFetch();

    await ruleCoverage(3, 42, { tableDefId: null, periodFrom: 202601, periodTo: 202612 });

    // ⛔ Саме ВІДСУТНІЙ параметр, а не `tableDefId=` порожнім: сервер трактує
    // відсутність як «усі таблиці», а порожній рядок не розбере в `int?`.
    expect(sent[0]?.url).not.toContain('tableDefId');
  });
});

describe('ruleCoverage: вікно за замовчуванням', () => {
  it('минулий і поточний календарні роки', () => {
    const window = defaultRuleCoverageWindow(new Date('2026-05-17T00:00:00Z'));

    expect(window).toEqual({ tableDefId: null, periodFrom: 202501, periodTo: 202699 });
  });

  it('порожнім вважається лише вікно, де нижня межа вища за верхню', () => {
    expect(ruleCoverageWindowEmpty(202612, 202601)).toBe(true);
    expect(ruleCoverageWindowEmpty(202601, 202601)).toBe(false);
    expect(ruleCoverageWindowEmpty(202601, 202612)).toBe(false);
  });
});

describe('ruleCoverage: комірки й правила', () => {
  it('порожня комірка — це null, а не рядок', () => {
    expect(ruleCoverageCell(null)).toBeNull();
    expect(ruleCoverageCell(undefined)).toBeNull();
    expect(ruleCoverageCell('')).toBeNull();
    expect(ruleCoverageCell('SO2')).toBe('SO2');
  });

  it('переможець не потрапляє в перелік решти правил', () => {
    expect(
      ruleCoverageOtherRules({
        values: ['CO2'],
        state: 'Conflict',
        winnerRuleCode: 'CO2_A',
        matchedRuleCodes: ['CO2_A', 'CO2_B'],
        rows: 3,
        documents: 2,
      }),
    ).toEqual(['CO2_B']);
  });

  it('розрив не має ні переможця, ні решти', () => {
    expect(
      ruleCoverageOtherRules({
        values: ['SO2'],
        state: 'Gap',
        winnerRuleCode: null,
        matchedRuleCodes: [],
        rows: 1,
        documents: 1,
      }),
    ).toEqual([]);
  });
});
