import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { CoverageEventsPanel } from '@/features/integration/CoverageEventsPanel';

/**
 * Події журналу покриття (ІНТ-3.3, `D-118`) на `/admin/sources`.
 *
 * ⛔ До цієї секції статусні рядки `itg.CollectionCoverage` не показував ЖОДЕН
 * екран: `GET /api/v1/collection-runs/coverage-events` не існувало, а деталь
 * прогону їх не бачить (`CollectionRunId = null`, Q-186).
 */

const Ceiling = {
  id: 9001,
  sourceEntityId: 42,
  sourceEntityCode: 'FLOW-01',
  sourceEntityName: 'Flow meter #1',
  dataSourceCode: 'PI-MAIN',
  periodKey: 202609,
  status: 'SkippedPointCeiling',
  details: 'Field TOTAL: 120000 points, ceiling 100000.',
  at: '2026-09-20T03:00:00Z',
};

const Closed = {
  ...Ceiling,
  id: 9000,
  sourceEntityName: null,
  sourceEntityCode: 'FLOW-02',
  status: 'SkippedPeriodClosed',
  details: null,
};

const EventsPath = '/api/v1/collection-runs/coverage-events';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

/** Підміняє fetch; повертає перелік параметрів кожного запиту до подій. */
function respond(pages: (cursor: string | null) => unknown): URLSearchParams[] {
  const seen: URLSearchParams[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = new URL(String(input), 'http://localhost');

      if (url.pathname === EventsPath) {
        seen.push(url.searchParams);
        return json(pages(url.searchParams.get('cursor')));
      }

      return json(null, 404);
    }),
  );

  return seen;
}

function show(entry = '/admin/sources'): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <CoverageEventsPanel />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CoverageEventsPanel', () => {
  it('рядок несе сутність, період, бейдж статусу і пояснення сервера як є', async () => {
    respond(() => ({ items: [Ceiling, Closed], nextCursor: null, totalCount: null }));
    show();

    await screen.findByText('Flow meter #1');

    const row = document.querySelector<HTMLElement>('tr[data-row-key="9001"]');
    expect(row, 'рядок події 9001').not.toBeNull();
    const scoped = within(row as HTMLElement);

    expect(scoped.getByText('PI-MAIN')).toBeTruthy();
    expect(scoped.getByText('202609')).toBeTruthy();
    expect(scoped.getByText('Field TOTAL: 120000 points, ceiling 100000.')).toBeTruthy();

    // ⛔ Статус — бейдж набору з тоном, а не сирий код сервера.
    const badge = (row as HTMLElement).querySelector<HTMLElement>('[data-status-kind="coverage"]');
    expect(badge?.dataset.statusState).toBe('SkippedPointCeiling');
    expect(badge?.dataset.statusTone).toBe('danger');
    expect(badge?.textContent).toBe('⟦status.coverage.SkippedPointCeiling⟧');

    // Сутність без назви — кодом; пояснення без тексту — тире.
    const other = within(document.querySelector<HTMLElement>('tr[data-row-key="9000"]') as HTMLElement);
    expect(other.getByText('FLOW-02')).toBeTruthy();
    expect(
      (document.querySelector('tr[data-row-key="9000"] [data-status-kind="coverage"]') as HTMLElement).dataset
        .statusTone,
    ).toBe('warning');
  });

  it('фільтр статусу з адреси (`coverageStatus`) іде в запит як `status`', async () => {
    const seen = respond(() => ({ items: [Closed], nextCursor: null, totalCount: null }));
    show('/admin/sources?coverageStatus=SkippedPeriodClosed&state=Failed');

    await screen.findByText('FLOW-02');

    expect(seen.length).toBeGreaterThan(0);
    expect(seen.every((params) => params.get('status') === 'SkippedPeriodClosed')).toBe(true);
  });

  it('конфлікт запису і потреба підтвердження — власні бейджі `warning`, не «ручне значення»', async () => {
    const WriteConflict = { ...Ceiling, id: 9002, sourceEntityCode: 'FLOW-03', sourceEntityName: null, status: 'SkippedWriteConflict' };
    const NeedsConfirmation = { ...Ceiling, id: 9003, sourceEntityCode: 'FLOW-04', sourceEntityName: null, status: 'SkippedNeedsConfirmation' };
    respond(() => ({ items: [WriteConflict, NeedsConfirmation], nextCursor: null, totalCount: null }));
    show();

    await screen.findByText('FLOW-03');

    for (const [id, status] of [
      ['9002', 'SkippedWriteConflict'],
      ['9003', 'SkippedNeedsConfirmation'],
    ] as const) {
      const badge = document.querySelector<HTMLElement>(`tr[data-row-key="${id}"] [data-status-kind="coverage"]`);
      expect(badge?.dataset.statusState, `стан рядка ${id}`).toBe(status);
      expect(badge?.dataset.statusTone, `тон рядка ${id}`).toBe('warning');
      expect(badge?.textContent).toBe(`⟦status.coverage.${status}⟧`);
    }
  });

  it('фільтр статусу пропонує обидва нові статуси з підписами каталогу', async () => {
    respond(() => ({ items: [], nextCursor: null, totalCount: null }));
    show();

    await screen.findByText('⟦coverageEvents.empty⟧');

    fireEvent.click(await screen.findByRole('textbox', { name: '⟦coverageEvents.filterStatus⟧' }));

    expect(await screen.findByRole('option', { name: '⟦status.coverage.SkippedWriteConflict⟧' })).toBeTruthy();
    expect(screen.getByRole('option', { name: '⟦status.coverage.SkippedNeedsConfirmation⟧' })).toBeTruthy();
  });

  it('події синку довідника — власні бейджі з тоном і без періоду (S5)', async () => {
    const expected = [
      ['9101', 'RegistryDiverged', 'warning'],
      ['9102', 'RegistryConflictKeptManual', 'info'],
      ['9103', 'RegistrySourceMissing', 'warning'],
      ['9104', 'RegistryElementUnlinked', 'warning'],
      ['9105', 'RegistryValueRejected', 'danger'],
      ['9106', 'RegistryPendingUpdate', 'info'],
    ] as const;
    const items = expected.map(([id, status], index) => ({
      ...Ceiling,
      id: Number(id),
      sourceEntityCode: `STACKS-${index}`,
      sourceEntityName: null,
      periodKey: null,
      status,
    }));
    respond(() => ({ items, nextCursor: null, totalCount: null }));
    show();

    await screen.findByText('STACKS-0');

    for (const [id, status, tone] of expected) {
      const row = document.querySelector<HTMLElement>(`tr[data-row-key="${id}"]`) as HTMLElement;
      const badge = row.querySelector<HTMLElement>('[data-status-kind="coverage"]');
      expect(badge?.dataset.statusState, `стан рядка ${id}`).toBe(status);
      expect(badge?.dataset.statusTone, `тон рядка ${id}`).toBe(tone);
      expect(badge?.textContent).toBe(`⟦status.coverage.${status}⟧`);
      // Довідник не живе за періодами — тире, а не вигаданий ключ.
      expect(within(row).getByText('—', { selector: 'p' })).toBeTruthy();
    }
  });

  it('події синку за політикою D-212 — власні бейджі з тоном і підписом каталогу', async () => {
    const expected = [
      ['9301', 'RegistryAutoCreated', 'info'],
      ['9302', 'RegistryDeactivated', 'warning'],
      ['9303', 'RegistryReactivated', 'warning'],
      ['9304', 'RegistryRuleViolation', 'warning'],
      ['9305', 'RegistryExternalKeyRelinked', 'warning'],
    ] as const;
    const items = expected.map(([id, status], index) => ({
      ...Ceiling,
      id: Number(id),
      sourceEntityCode: `AF-${index}`,
      sourceEntityName: null,
      periodKey: null,
      status,
    }));
    respond(() => ({ items, nextCursor: null, totalCount: null }));
    show();

    await screen.findByText('AF-0');

    for (const [id, status, tone] of expected) {
      const row = document.querySelector<HTMLElement>(`tr[data-row-key="${id}"]`) as HTMLElement;
      const badge = row.querySelector<HTMLElement>('[data-status-kind="coverage"]');
      expect(badge?.dataset.statusState, `стан рядка ${id}`).toBe(status);
      expect(badge?.dataset.statusTone, `тон рядка ${id}`).toBe(tone);
      expect(badge?.textContent).toBe(`⟦status.coverage.${status}⟧`);
    }
  });

  it('відмова джерела віддати дані — бейдж `danger` і причина конвертом, резолвлена каталогом', async () => {
    // ⛔ Застряглий інтервал збору (джерело відповідає, але дані нечитабельні)
    // раніше не лишав у журналі покриття нічого. `details` — конверт: без
    // `collectionRunErrorText` у панелі тут стояв би сирий JSON.
    const details = JSON.stringify({
      k: 'coverageEvents.sourceDataRefused',
      p: { path: 'tagA', from: '2026-02-28T12:00:00.0000000Z', to: '2026-03-01T12:00:00.0000000Z', key: '0123456789ABCDEF' },
      i: {
        k: 'jobs.collectionRunReason',
        p: { code: 'ECR-INT-0422' },
        i: { k: 'err.ECR-INT-0422.timestampUnreadable', p: { dataSource: 'PIAF', sourcePath: 'tagA', valueType: 'NULL' } },
      },
    });
    const Refused = { ...Ceiling, id: 9201, sourceEntityCode: 'STACK-1', sourceEntityName: null, periodKey: null, status: 'SourceDataRefused', details };
    respond(() => ({ items: [Refused], nextCursor: null, totalCount: null }));
    show();

    await screen.findByText('STACK-1');

    const row = document.querySelector<HTMLElement>('tr[data-row-key="9201"]') as HTMLElement;
    const badge = row.querySelector<HTMLElement>('[data-status-kind="coverage"]');
    expect(badge?.dataset.statusState).toBe('SourceDataRefused');
    expect(badge?.dataset.statusTone).toBe('danger');
    expect(badge?.textContent).toBe('⟦status.coverage.SourceDataRefused⟧');

    expect(
      within(row).getByText(
        '⟦coverageEvents.sourceDataRefused (path=tagA, from=2026-02-28T12:00:00.0000000Z, to=2026-03-01T12:00:00.0000000Z, key=0123456789ABCDEF, '
          + 'message=⟦jobs.collectionRunReason (code=ECR-INT-0422, '
          + 'message=⟦err.ECR-INT-0422.timestampUnreadable (dataSource=PIAF, sourcePath=tagA, valueType=NULL)⟧)⟧)⟧',
      ),
    ).toBeTruthy();
    expect(within(row).queryByText(details)).toBeNull();
  });

  it('фільтр статусу пропонує відмову джерела з підписом каталогу', async () => {
    respond(() => ({ items: [], nextCursor: null, totalCount: null }));
    show();

    await screen.findByText('⟦coverageEvents.empty⟧');

    fireEvent.click(await screen.findByRole('textbox', { name: '⟦coverageEvents.filterStatus⟧' }));

    expect(await screen.findByRole('option', { name: '⟦status.coverage.SourceDataRefused⟧' })).toBeTruthy();
  });

  it('фільтр статусу пропонує події синку довідника з підписами каталогу', async () => {
    respond(() => ({ items: [], nextCursor: null, totalCount: null }));
    show();

    await screen.findByText('⟦coverageEvents.empty⟧');

    fireEvent.click(await screen.findByRole('textbox', { name: '⟦coverageEvents.filterStatus⟧' }));

    for (const status of [
      'RegistryDiverged',
      'RegistryConflictKeptManual',
      'RegistrySourceMissing',
      'RegistryElementUnlinked',
      'RegistryValueRejected',
      'RegistryPendingUpdate',
      'RegistryAutoCreated',
      'RegistryDeactivated',
      'RegistryReactivated',
      'RegistryRuleViolation',
      'RegistryExternalKeyRelinked',
    ]) {
      expect(await screen.findByRole('option', { name: `⟦status.coverage.${status}⟧` })).toBeTruthy();
    }
  });

  it('фільтр `coverageStatus=RegistrySourceMissing` іде в запит як `status`', async () => {
    const seen = respond(() => ({ items: [], nextCursor: null, totalCount: null }));
    show('/admin/sources?coverageStatus=RegistrySourceMissing');

    await screen.findByText('⟦coverageEvents.empty⟧');

    expect(seen.length).toBeGreaterThan(0);
    expect(seen.every((params) => params.get('status') === 'RegistrySourceMissing')).toBe(true);
  });

  it('фільтр `coverageStatus=SkippedWriteConflict` іде в запит як `status`', async () => {
    const seen = respond(() => ({ items: [], nextCursor: null, totalCount: null }));
    show('/admin/sources?coverageStatus=SkippedWriteConflict');

    await screen.findByText('⟦coverageEvents.empty⟧');

    expect(seen.length).toBeGreaterThan(0);
    expect(seen.every((params) => params.get('status') === 'SkippedWriteConflict')).toBe(true);
  });

  it('«Показати ще» дочитує за курсором і зникає, коли курсора немає', async () => {
    const seen = respond((cursor) =>
      cursor === null
        ? { items: [Ceiling], nextCursor: 'c-1', totalCount: null }
        : { items: [Closed], nextCursor: null, totalCount: null },
    );
    show();

    await screen.findByText('Flow meter #1');
    const more = document.querySelector<HTMLElement>('[data-coverage-events-more]');
    expect(more).not.toBeNull();

    fireEvent.click(more as HTMLElement);

    await screen.findByText('FLOW-02');
    expect(screen.getByText('Flow meter #1')).toBeTruthy();
    expect(seen.at(-1)?.get('cursor')).toBe('c-1');
    await waitFor(() => expect(document.querySelector('[data-coverage-events-more]')).toBeNull());
  });

  it('порожній журнал — власний порожній стан, без кнопки дочитування', async () => {
    respond(() => ({ items: [], nextCursor: null, totalCount: null }));
    show();

    expect(await screen.findByText('⟦coverageEvents.empty⟧')).toBeTruthy();
    expect(document.querySelector('[data-coverage-events-more]')).toBeNull();
  });
});
