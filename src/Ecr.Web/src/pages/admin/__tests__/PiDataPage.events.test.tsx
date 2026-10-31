import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { PiDataPage } from '@/pages/admin/PiDataPage';
import { testTheme } from '@/test/render';

/**
 * D-465 «Дані з PI» → «Події»: сутність-шаблон подій відкривається на подіях (лінивий чанк); мапінг невидимого
 * документа — без назви документа; подія з рядком у видимому документі — посилання, у невидимому — «записано в
 * документ, до якого у вас немає доступу»; клік по події — атрибути з порівнянням і причиною; журнал за період і
 * журнал видалень; перемикач «Часові ряди» повертає теги.
 */
const requested: string[] = [];

const Attribute = {
  columnDefId: 2,
  columnCode: 'VOL',
  attribute: 'Volume',
  scope: 'Event',
  piValueNumeric: '3.5000000000000000',
  piValueText: null,
  piValueTruncated: false,
  piUnit: 'Sm3',
  piChangedAt: '2026-10-01T00:00:00Z',
  expectedValue: '3.5',
  cellValue: '1',
  cellOrigin: 'Integration',
  comparison: 'NotWritten',
  reason: 'SheetLocked',
};

function event(id: string, row: boolean, hidden: boolean): unknown {
  return {
    linkId: id === 'MAY' ? 2 : 1,
    mapId: 11,
    eventId: id,
    eventName: `Flaring ${id}`,
    startUtc: '2026-05-20T06:00:00Z',
    startLocal: '2026-05-20T11:00:00+05:00',
    endUtc: '2026-05-20T07:00:00Z',
    endLocal: '2026-05-20T12:00:00+05:00',
    timeZoneId: 'Asia/Atyrau',
    primaryElement: 'FLARE-1',
    sourceModifiedUtc: '2026-05-21T00:00:00Z',
    status: 'Synced',
    reason: 'SheetLocked',
    periodKey: 202601,
    row: row ? { documentId: 7, documentKey: 'DOC-7', periodKey: 202601, tableInstanceId: 70, rowKey: `EF-${id}` } : null,
    rowHidden: hidden,
    firstSeenAt: '2026-05-21T00:00:00Z',
    lastSeenAt: '2026-10-01T00:00:00Z',
    lastSyncAt: '2026-10-01T00:00:00Z',
    attributes: [Attribute],
  };
}

function respond(url: string): unknown {
  if (url === '/api/v1/sources') return [{ id: 4, code: 'FlareEvent', displayName: 'Flare events', dataSourceId: 1 }];
  if (url.startsWith('/api/v1/sources/4/raw-data/tags'))
    return {
      sourceEntityId: 4,
      code: 'FlareEvent',
      displayName: 'Flare events',
      isActive: true,
      isTruncated: false,
      eventMapCount: 2,
      tags: [{ sourcePath: 'Flare|V', mapId: null, targetColumnCode: null, targetRowKey: null, aggregation: null, sourceUnitCode: null, isActive: true }],
    };
  if (url.startsWith('/api/v1/sources/4/raw-data/events/maps'))
    return {
      sourceEntityId: 4,
      code: 'FlareEvent',
      displayName: 'Flare events',
      maps: [
        { mapId: 11, isActive: true, documentVisible: true, documentId: 7, documentKey: 'DOC-7', tableCode: 'FLARES', fields: [], hasMonthColumnFields: true },
        { mapId: 12, isActive: false, documentVisible: false, documentId: null, documentKey: null, tableCode: 'FLARES', fields: [], hasMonthColumnFields: false },
      ],
      periods: [{ periodKey: 202601, state: 'Open', fromUtc: '2025-12-31T19:00:00Z', toUtc: '2026-12-31T19:00:00Z' }],
    };
  if (url.startsWith('/api/v1/sources/4/raw-data/events/removed'))
    return {
      isTruncated: false,
      items: [
        { removedAt: '2026-09-01T00:00:00Z', eventId: 'FEB', eventName: 'Flaring FEB', eventStartUtc: '2026-02-10T06:00:00Z', periodKey: 202601, mapId: 12, row: null, rowHidden: true, runId: 'abc' },
      ],
    };
  if (url.startsWith('/api/v1/sources/4/raw-data/events?'))
    return { items: [event('MAY', true, false), event('JUL', false, true)], nextCursor: null, totalCount: 2 };
  if (url.startsWith('/api/v1/collection-runs/coverage-events')) return { items: [], nextCursor: null, totalCount: null };
  return null;
}

function show(path: string): void {
  requested.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      requested.push(url);
      return new Response(JSON.stringify(respond(url)), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[path]}>
          <PiDataPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('PiDataPage — події PI (D-465)', () => {
  it('шаблон подій відкривається на подіях: рядок, прихований документ, атрибути з причиною, журнали', async () => {
    show('/admin/pi-data?entity=4');

    expect(await screen.findByText('DOC-7')).toBeTruthy();
    expect(screen.getAllByText('⟦piEvents.rowHidden⟧').length).toBeGreaterThan(0);
    expect(screen.getByText('⟦piEvents.monthColumnWarning⟧')).toBeTruthy();
    expect(await screen.findByText('Flaring FEB')).toBeTruthy();
    expect(screen.getByText('⟦coverageEvents.empty⟧')).toBeTruthy();

    // Часові ряди не запитуються, поки показано події.
    expect(requested.some((url) => url.includes('/raw-data/series'))).toBe(false);
    const list = requested.find((url) => url.startsWith('/api/v1/sources/4/raw-data/events?')) ?? '';
    const query = new URLSearchParams(list.split('?')[1]);
    expect(query.get('mapId')).toBe('11');
    expect(query.get('limit')).toBe('50');
    expect(requested.some((url) => url.includes('coverage-events?') && url.includes('periodKey=202601'))).toBe(true);

    fireEvent.click(screen.getByText('Flaring MAY'));
    const detail = await screen.findByText('⟦piEvents.attributesTitle (event=Flaring MAY)⟧');
    expect(detail).toBeTruthy();
    expect(document.querySelector('[data-pi-event-comparison="NotWritten"]')).not.toBeNull();
    expect(screen.getAllByText('⟦piEvents.reasonSheetLocked⟧').length).toBeGreaterThan(0);
    expect(screen.getByText('3.5 Sm3')).toBeTruthy();
  });

  it('мапінг невидимого документа — без назви документа, мапінг з адреси йде в запит', async () => {
    show('/admin/pi-data?entity=4&map=12');

    await waitFor(() =>
      expect(requested.some((url) => url.startsWith('/api/v1/sources/4/raw-data/events?') && url.includes('mapId=12'))).toBe(true),
    );
    expect(await screen.findByDisplayValue('⟦piEvents.documentHidden⟧ · FLARES (⟦piEvents.mapPaused⟧)')).toBeTruthy();
  });

  it('перемикач «Часові ряди» повертає теги й графік', async () => {
    show('/admin/pi-data?entity=4&view=series');

    await waitFor(() => expect(requested.some((url) => url.startsWith('/api/v1/sources/4/raw-data/series?'))).toBe(true));
    expect(requested.some((url) => url.includes('/raw-data/events'))).toBe(false);
  });
});
