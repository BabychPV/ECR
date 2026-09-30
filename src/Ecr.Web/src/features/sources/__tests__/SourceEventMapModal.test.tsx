import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { testTheme } from '@/test/render';
import { SourceEventMapModal } from '../SourceEventMapModal';
import type { SourceEventMap } from '../sourceEventsApi';

/**
 * Форма мапінгу подій (HSE301 A6-UI) проти заглушки `fetch`: новий мапінг не зберігається, доки відома наперед
 * відмова (`$start`/`$end`, документ, таблиця); наявний — `PUT` рівно з тим, що в сітці; проба йде з шаблоном
 * сутності й атрибутами мапінгу, а незіставлене значення PI додається в таблицю відповідностей одним кліком.
 */

interface Sent {
  readonly method: string;
  readonly path: string;
  readonly body: unknown;
}

let sent: Sent[] = [];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const Stored: SourceEventMap = {
  id: 12,
  sourceEntityId: 42,
  documentId: 100,
  tableDefId: 200,
  volumeMode: 'None',
  filterAttribute: null,
  filterScope: null,
  filterValue: null,
  isActive: true,
  fields: [
    { id: 1, targetColumnDefId: 1, sourceAttribute: '$start', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
    { id: 2, targetColumnDefId: 2, sourceAttribute: '$end', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
    {
      id: 3,
      targetColumnDefId: 3,
      sourceAttribute: 'TUGF_Category',
      attributeScope: 'Event',
      valueKind: 'ValueMap',
      sourceUnitId: null,
      targetUnitId: null,
      values: [{ id: 5, sourceValue: 'V6', registryEntryId: 700 }],
    },
  ],
};

function respond(): void {
  sent = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), 'http://localhost');
      const path = url.pathname;
      const method = init?.method ?? 'GET';
      const body: unknown = init?.body === undefined || init.body === null ? undefined : JSON.parse(String(init.body));
      sent.push({ method, path, body });

      if (path === '/api/v1/data-sources/7/event-templates') {
        return json([
          {
            templateName: 'FLARE_EVENTS',
            attributes: [{ name: 'TUGF_Category', scope: 'Event', dataType: 'String', sourceUnitSymbol: null }],
          },
        ]);
      }
      if (path === '/api/v1/documents/100/tables') {
        return json([
          { tableDefId: 200, tableInstanceId: 300, allowsDynamicRows: true, tableCode: 'FLARE_RECORD', sheetCode: 'S1' },
          { tableDefId: 201, tableInstanceId: 301, allowsDynamicRows: false, tableCode: 'FIXED', sheetCode: 'S1' },
        ]);
      }
      if (path === '/api/v1/documents/100/tables/300') {
        return json({
          columns: [
            { id: 1, code: 'Start', header: 'Start', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
            { id: 2, code: 'End', header: 'End', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
            { id: 3, code: 'Category', header: 'Category', dataType: 'Lookup', lookupRegistryDefId: 70, unitId: null },
          ],
        });
      }
      if (path === '/api/v1/units') return json([]);
      if (path === '/api/v1/registries') return json([{ id: 70, code: 'TUGF_CATEGORY' }]);
      if (path === '/api/v1/registries/TUGF_CATEGORY/entries') {
        return json([{ id: 700, code: 'V6', display: 'Category V6', parentEntryId: null, validFrom: null, validTo: null }]);
      }
      if (path === '/api/v1/data-sources/7/probe-events') {
        return json({
          fromUtc: '2026-09-01T00:00:00Z',
          toUtc: '2026-09-30T00:00:00Z',
          truncated: false,
          errorCode: null,
          events: [
            {
              eventId: 'e-1',
              name: 'Flaring',
              templateName: 'FLARE_EVENTS',
              startUtc: '2026-09-29T04:00:00Z',
              endUtc: '2026-09-29T04:10:00Z',
              modifiedUtc: null,
              parentId: null,
              primaryElementPath: null,
              attributes: [
                { name: 'TUGF_Category', scope: 'Event', valueString: 'V6', valueNumeric: null, sourceUnitSymbol: null },
                { name: 'TUGF_Category', scope: 'Event', valueString: 'V8', valueNumeric: null, sourceUnitSymbol: null },
              ],
            },
          ],
        });
      }
      if (path === '/api/v1/source-event-maps/12' && method === 'PUT') return json({ ...Stored, ...(body as object) });

      return json(null);
    }),
  );
}

function show(map: SourceEventMap | null, onClose = vi.fn()): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SourceEventMapModal
            dataSourceId={7}
            sourceEntityId={42}
            template="FLARE_EVENTS"
            map={map}
            documents={[{ id: 100, businessKey: 'DOC-000123' } as never]}
            onClose={onClose}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('SourceEventMapModal', () => {
  it('новий мапінг: «Зберегти» вимкнено й названо чому — документ, таблиця, $start/$end', async () => {
    respond();
    show(null);

    const form = await screen.findByRole('dialog');
    const save = within(form).getByRole('button', { name: '⟦common.save⟧' });

    expect(save.hasAttribute('disabled')).toBe(true);
    for (const problem of ['documentRequired', 'tableRequired', 'startEndRequired']) {
      expect(form.querySelector(`[data-source-event-map-problem="${problem}"]`)).not.toBeNull();
    }
    // $start і $end — уже рядками сітки, людина лише обирає колонку.
    expect(form.querySelector('[data-source-event-field="$start"]')).not.toBeNull();
    expect(form.querySelector('[data-source-event-field="$end"]')).not.toBeNull();
  });

  it('наявний мапінг: документ і таблиця вимкнені, «Зберегти» шле PUT із полями й відповідностями', async () => {
    respond();
    const onClose = vi.fn();
    show(Stored, onClose);

    const form = await screen.findByRole('dialog');
    await waitFor(() => expect(sent.some((s) => s.path === '/api/v1/documents/100/tables/300')).toBe(true));

    expect(form.querySelector<HTMLInputElement>('[data-source-event-map-document]')?.disabled).toBe(true);
    expect(form.querySelector<HTMLInputElement>('[data-source-event-map-table]')?.disabled).toBe(true);

    const save = within(form).getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => expect(save.hasAttribute('disabled')).toBe(false));
    fireEvent.click(save);

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    const put = sent.find((s) => s.method === 'PUT');
    expect(put?.path).toBe('/api/v1/source-event-maps/12');
    expect(put?.body).toEqual({
      volumeMode: 'None',
      isActive: true,
      filterAttribute: null,
      filterScope: null,
      filterValue: null,
      fields: [
        { targetColumnDefId: 1, sourceAttribute: '$start', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: null },
        { targetColumnDefId: 2, sourceAttribute: '$end', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: null },
        {
          targetColumnDefId: 3,
          sourceAttribute: 'TUGF_Category',
          attributeScope: 'Event',
          valueKind: 'ValueMap',
          sourceUnitId: null,
          targetUnitId: null,
          values: [{ sourceValue: 'V6', registryEntryId: 700 }],
        },
      ],
    });
  });

  it('проба: шаблон сутності й атрибути мапінгу; незіставлене значення PI — у таблицю відповідностей одним кліком', async () => {
    respond();
    show(Stored);

    const form = await screen.findByRole('dialog');
    fireEvent.click(within(form).getByRole('button', { name: '⟦sourceEvents.probeRun⟧' }));

    await waitFor(() => expect(form.querySelector('[data-source-event-probe-row="e-1"]')).not.toBeNull());
    const probe = sent.find((s) => s.path === '/api/v1/data-sources/7/probe-events');
    expect(probe?.method).toBe('POST');
    expect(probe?.body).toMatchObject({
      template: 'FLARE_EVENTS',
      attributes: [{ name: 'TUGF_Category', scope: 'Event' }],
      maxEvents: 20,
    });

    // V6 уже зіставлено — пропонується лише V8.
    expect(form.querySelector('[data-source-event-probe-add="V6"]')).toBeNull();
    fireEvent.click(form.querySelector<HTMLElement>('[data-source-event-probe-add="V8"]')!);

    await waitFor(() => expect(form.querySelectorAll('[data-source-event-value-pair]')).toHaveLength(2));
    // Нова пара без запису довідника — зберегти не можна, доки його не обрано.
    expect(form.querySelector('[data-source-event-map-problem="valueMapIncomplete"]')).not.toBeNull();
  });
});
