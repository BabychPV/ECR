import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { formatPeriodKey } from '@/shared/format/periodKey';
import { SheetLog } from '../SheetLog';

/**
 * ✎ LAND-YEAR L7b (п. 6 плану): журнал аркуша документа «рік» — усі місяці року (`?year=true`, сервер L6 зливає за
 * часом), і кожен рядок (правка, рядок, дія процесу) називає свій місяць. Копія на місяць — журнал одного періоду без
 * позначки місяця й без прапора в запиті.
 *
 * ⛔ Мутаційний доказ (READY): `year` у `useSheetActivity` → `false` — червоний «запит з year=true»; `month` у рядку →
 * `null` — червоний «рядки називають місяць».
 */
const Base = {
  at: '2099-10-29T10:00:00Z',
  byDisplayName: 'f13-operator',
  origin: 'UserEdit',
  rowKey: 'R1',
  rowLabelL10n: null,
  columnDefId: 11,
  columnCode: 'M10',
  columnHeaderL10n: null,
  columnDataType: 'Decimal',
  tableCode: 'T1',
  tableNameL10n: null,
  oldValue: null,
  newValue: '5',
  action: null,
  fromState: null,
  toState: null,
  reason: null,
};

const October = { ...Base, kind: 'CellEdit', periodKey: 209910 };
const September = { ...Base, kind: 'RowChange', periodKey: 209909, action: 'RowAdded', rowKey: 'NEW_1', columnCode: null };
const SubmitAug = { ...Base, kind: 'Workflow', periodKey: 209908, action: 'Submit', rowKey: null, columnCode: null };

const urls: string[] = [];

function mount(year: boolean | undefined): void {
  urls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input));
      return new Response(JSON.stringify({ items: [October, September, SubmitAug], nextCursor: null }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SheetLog
          documentId={1}
          periodKey={209901}
          sheet={{ sheetDefId: 2, code: 'S', name: 'Sheet' }}
          onOpenCell={() => undefined}
          {...(year === undefined ? {} : { year })}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SheetLog: документ «рік»', () => {
  it('рік: запит з year=true, кожен рядок називає свій місяць (правка, рядок, подання)', async () => {
    mount(true);

    await screen.findByText('NEW_1', { exact: false });
    expect(urls.every((url) => url.includes('&year=true'))).toBe(true);

    const months = Array.from(document.querySelectorAll('[data-inspector-log-period]'));
    expect(months.map((node) => node.getAttribute('data-inspector-log-period'))).toEqual(['209910', '209909', '209908']);
    expect(months.map((node) => node.textContent)).toEqual([
      formatPeriodKey(209910),
      formatPeriodKey(209909),
      formatPeriodKey(209908),
    ]);
    // Позначка — усередині свого рядка (рядок списку), а не окремим пунктом.
    expect(months[0]?.closest('[data-inspector-sheet-event]')?.getAttribute('data-inspector-sheet-event')).toBe('CellEdit');
  });

  it('копія на місяць: без year=true і без позначки місяця', async () => {
    mount(undefined);

    await screen.findByText('NEW_1', { exact: false });
    expect(urls.some((url) => url.includes('year=true'))).toBe(false);
    expect(document.querySelector('[data-inspector-log-period]')).toBeNull();
  });
});
