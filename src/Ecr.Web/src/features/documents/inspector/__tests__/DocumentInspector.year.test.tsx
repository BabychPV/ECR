import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { testTheme } from '@/test/render';
import { DocumentInspector } from '../DocumentInspector';
import { clearInspectedCell, publishInspectedCell, type InspectedCell } from '../inspectedCell';

/**
 * ✎ LAND-YEAR L7a/L7b — інспектор документа «рік» (`YearWithMonthColumns`).
 *
 * Issues: зауваження всіх місяців року, кожне з назвою місяця прогону (`periodKey` з `validation?year=true`); шапка —
 * «Year 2099». History: журнал аркуша з `year=true`. Info: місяць-власник комірки (місячна колонка — її місяць, інша —
 * рік) і зауваження таблиці за рік (агрегат річного результату, не `tables/status` якоря).
 * Кожна гілка — у парі з копією на місяць (як до L7).
 *
 * ⛔ Мутаційний доказ (READY): умова чипа місяця `yearLayout &&` → `false &&`; `periodLabel` → завжди місяць;
 * `year={yearLayout}` журналу → `false`; рядок «Period» Info `year === undefined` → завжди `[]`; `yearTableIssues` →
 * `tables/status`.
 */

const Anchor = 209901;

const Tables: DocumentTableDto[] = [
  {
    allowsDynamicRows: false, copyOrdinal: 1, isCopyable: false, maxDynamicRows: null, sheetCode: 'A', sheetDefId: 101,
    sheetNameL10n: { values: { en: 'Sheet A' } }, sheetOrdinal: 0, tableCode: 'T1', tableDefId: 1, tableInstanceId: 10,
    tableNameL10n: { values: { en: 'Land table' } }, tableOrdinal: 0,
  } as DocumentTableDto,
];

const YearFindings: ValidationFindingDto[] = [
  { severity: 'Error', ruleCode: 'REQ', message: 'October is empty', tableDefId: 1, rowKey: 'R1', columnCode: 'M10', blocksSave: false, periodKey: 209910 },
  { severity: 'Error', ruleCode: 'REQ', message: 'September is empty', tableDefId: 1, rowKey: 'R2', columnCode: 'M09', blocksSave: false, periodKey: 209909 },
  { severity: 'Warning', ruleCode: 'HRS', message: 'Hours high', tableDefId: 1, rowKey: 'R3', columnCode: 'M09', blocksSave: false, periodKey: 209909 },
];

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

const urls: string[] = [];

function stubFetch(): void {
  urls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      urls.push(url);
      // Стан таблиці за ЯКІР: тут помилок 7 — рік їх не показує (у річному результаті їх 2).
      if (url.includes('/tables/status')) {
        return jsonResponse([{ tableDefId: 1, sheetCode: 'A', inputCells: 4, filledCells: 2, errorCount: 7, warningCount: 0 }]);
      }
      if (url.includes('/activity')) return jsonResponse({ items: [], nextCursor: null });

      // Решта (джерело комірки, дозвіл рядка) — "немає", як у DocumentInspector.test.
      return new Response(null, { status: 404 });
    }),
  );
}

function show(panel: string, yearLayout: boolean | undefined, messages: ValidationFindingDto[] | null = YearFindings): void {
  stubFetch();
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[`/documents/1?panel=${panel}`]}>
          <DocumentInspector
            documentId={1}
            periodKey={Anchor}
            {...(yearLayout === undefined ? {} : { yearLayout })}
            tables={Tables}
            messages={messages}
            onSelectFinding={() => undefined}
            sheet={{ sheetDefId: 101, code: 'A', name: 'Sheet A' }}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const Cell: InspectedCell = {
  tableInstanceId: 10,
  periodKey: Anchor,
  ownerPeriodKey: 209910,
  rowKey: 'R1',
  rowLabel: 'Gas',
  columnCode: 'M10',
  columnDefId: 33,
  columnHeader: 'October',
  unitSymbol: null,
  dataType: 'Decimal',
  scale: 2,
  isCalculated: false,
  isReadOnly: false,
  value: '5',
};

const chip = (): string | null | undefined => document.querySelector('[data-inspector-period]')?.textContent;

afterEach(() => {
  cleanup();
  act(() => clearInspectedCell());
  vi.unstubAllGlobals();
});

describe('Issues документа «рік»', () => {
  it('рік: шапка — «Year 2099», кожне зауваження називає свій місяць', async () => {
    show('issues', true);

    await screen.findByText('October is empty');
    expect(chip()).toContain('calcResults.yearChip (year=2099)');
    const months = Array.from(document.querySelectorAll('[data-inspector-finding-period]'));
    expect(months.map((node) => node.getAttribute('data-inspector-finding-period'))).toEqual(['209910', '209909', '209909']);
    expect(months[0]?.textContent).toBe(formatPeriodKey(209910));
    expect(screen.getByText('September is empty')).toBeTruthy();
  });

  it('копія на місяць: шапка — місяць, позначок місяця в зауваженнях немає', async () => {
    show('issues', undefined);

    await screen.findByText('October is empty');
    expect(chip()).toBe(formatPeriodKey(Anchor));
    expect(document.querySelector('[data-inspector-finding-period]')).toBeNull();
  });

  it('рік без зауважень: «перевірено» називає рік, не місяць-якір', async () => {
    show('issues', true, []);

    const hint = await waitFor(() => {
      const found = document.querySelector('.ecr-insp-empty span');
      if (found === null) throw new Error('not yet');
      return found;
    });
    expect(hint.textContent).toContain('noIssuesChecked (period=⟦calcResults.yearChip (year=2099)⟧)');
  });
});

describe('History документа «рік»', () => {
  it('рік: журнал аркуша — з year=true', async () => {
    show('history', true);

    await waitFor(() => expect(urls.some((url) => url.includes('/sheets/101/activity'))).toBe(true));
    expect(urls.filter((url) => url.includes('/activity')).every((url) => url.includes('&year=true'))).toBe(true);
  });

  it('копія на місяць: журнал аркуша — без year=true', async () => {
    show('history', undefined);

    await waitFor(() => expect(urls.some((url) => url.includes('/sheets/101/activity'))).toBe(true));
    expect(urls.some((url) => url.includes('year=true'))).toBe(false);
  });
});

describe('Info документа «рік»', () => {
  it('рік: місячна колонка — її місяць-власник; зауваження таблиці — за рік (2 помилки, 1 попередження)', async () => {
    show('info', true);
    act(() => publishInspectedCell(Cell));

    const info = await waitFor(() => {
      const found = document.querySelector('[data-inspector-info]');
      if (found === null) throw new Error('not yet');
      return found;
    });
    expect(info.textContent).toContain('inspector.info.period');
    expect(info.textContent).toContain(formatPeriodKey(209910));
    expect(info.textContent).toContain('tableIssuesValue (errors=2, warnings=1)');
  });

  it('рік: не-місячна колонка — належить року', async () => {
    show('info', true);
    act(() => publishInspectedCell({ ...Cell, ownerPeriodKey: null, columnCode: 'NAME' }));

    const info = await waitFor(() => {
      const found = document.querySelector('[data-inspector-info]');
      if (found === null) throw new Error('not yet');
      return found;
    });
    expect(info.textContent).toContain('calcResults.yearChip (year=2099)');
  });

  it('рік, не перевіряли: зауваження таблиці — «—», не 0', async () => {
    show('info', true, null);
    act(() => publishInspectedCell(Cell));

    const info = await waitFor(() => {
      const found = document.querySelector('[data-inspector-info]');
      if (found === null) throw new Error('not yet');
      return found;
    });
    expect(info.textContent).toContain('tableIssuesValue (errors=—, warnings=—)');
  });

  it('копія на місяць: рядка «Period» немає, зауваження таблиці — зі стану таблиці періоду', async () => {
    show('info', undefined);
    act(() => publishInspectedCell(Cell));

    await waitFor(() => {
      const found = document.querySelector('[data-inspector-info]');
      expect(found?.textContent).toContain('tableIssuesValue (errors=7, warnings=0)');
    });
    expect(document.querySelector('[data-inspector-info]')?.textContent).not.toContain('inspector.info.period');
  });
});
