import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import type { RowWindowMap } from '@/features/sources/rowWindowApi';
import type { SourceEventMap } from '@/features/sources/sourceEventsApi';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { RowWindowMapsPanel } from '../RowWindowMapsPanel';
import { SourceEventMapModal } from '../SourceEventMapModal';

/**
 * B149: «Row window map» і «Source event map» — один запис на «Save» (зонд: ЗАКРИТО, база 826f654e4).
 *
 * Власники монтують обидва діалоги лише за `{canManage && editing !== null && …}` (`RowWindowMapsPanel`,
 * `SourceEventsTab`), самі діалоги — `<Modal opened>` без закритого стану, `onSuccess` закриває одразу. «Save» —
 * `loading={save.isPending}`: кнопка вимкнена з наступним рендером, тож другий клік подвійного кліку (наступний тік)
 * нічого не шле. RowWindowMapModal зондовано через справжнього власника `RowWindowMapsPanel`.
 *
 * Тест тримає це: `loading={save.isPending}` → `loading={false}` — червоний відповідний тест.
 */

const Columns = {
  columns: [
    { id: 1, code: 'Start', header: 'Start', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
    { id: 2, code: 'End', header: 'End', dataType: 'Date', lookupRegistryDefId: null, unitId: null },
    { id: 3, code: 'Volume', header: 'Volume', dataType: 'Decimal', lookupRegistryDefId: null, unitId: null },
    { id: 4, code: 'Key', header: 'Key', dataType: 'String', lookupRegistryDefId: null, unitId: null },
  ],
};
const Tables = [{ tableDefId: 200, tableInstanceId: 300, allowsDynamicRows: true, tableCode: 'FLARE_RECORD', sheetCode: 'S1' }];

const Window: RowWindowMap = {
  id: 5, sourceEntityId: 42, documentId: 100, tableDefId: 200, tableCode: 'FLARE_RECORD', targetColumnDefId: 3, targetColumnCode: 'Volume',
  startColumnDefId: 1, startColumnCode: 'Start', endColumnDefId: 2, endColumnCode: 'End', selectorColumnDefId: 4, selectorColumnCode: 'Key',
  summary: 'Total', isStep: false, maxGapSeconds: null, targetUnitId: 9, minPercentGood: '95.00', refetchWithinDays: 7, isActive: true,
  rowVersion: '00000000000007D1', sources: [{ id: 1, selectorValue: 'A', sourceEntityId: 42, sourceField: 'Flare.Total', sourceUnitId: 8 }],
} as unknown as RowWindowMap;

const EventMap: SourceEventMap = {
  id: 12, sourceEntityId: 42, documentId: 100, tableDefId: 200, volumeMode: 'None', filterAttribute: null, filterScope: null, filterValue: null,
  isActive: true, rowVersion: '00000000000007D1',
  fields: [
    { id: 1, targetColumnDefId: 1, sourceAttribute: '$start', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
    { id: 2, targetColumnDefId: 2, sourceAttribute: '$end', attributeScope: 'Event', valueKind: 'Direct', sourceUnitId: null, targetUnitId: null, values: [] },
  ],
} as unknown as SourceEventMap;

function read(url: string): unknown {
  if (url === '/api/v1/row-window-maps') return [Window];
  if (url === '/api/v1/documents/100/tables') return Tables;
  if (url === '/api/v1/documents/100/tables/300') return Columns;
  if (url === '/api/v1/units') return [{ id: 9, code: 'Sm3' }, { id: 8, code: 'kg' }];
  if (url === '/api/v1/data-sources/7/event-templates') return [{ templateName: 'FLARE_EVENTS', attributes: [] }];

  return [];
}

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: діалоги мапінгу джерел — один запис на «Save»', () => {
  it('RowWindowMapModal (власник RowWindowMapsPanel): подвійний клік шле один PUT; після успіху діалог знято', async () => {
    const server = holdWrites({ read, write: () => ({ body: Window }) });
    renderKeepMounted(
      <RowWindowMapsPanel sourceEntityId={42} entities={[{ id: 42, code: 'FLARE_EVENTS', displayName: 'Flare events' } as never]} documents={[{ id: 100, businessKey: 'DOC-000123' } as never]} canManage />,
    );

    const edit = await waitFor(() => {
      const found = document.querySelector<HTMLElement>('[data-row-window-edit="5"]');
      if (found === null) throw new Error('рядка ще немає');
      return found;
    });
    fireEvent.click(edit);
    const form = await screen.findByRole('dialog');
    fireEvent.click(form.querySelector<HTMLElement>('[data-row-window-document]')!);
    fireEvent.click(await screen.findByRole('option', { name: 'DOC-000123' }));
    const save = within(form).getByRole('button', { name: /common\.save/ });
    await waitFor(() => {
      expect(save.hasAttribute('disabled')).toBe(false);
    });

    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['PUT /api/v1/row-window-maps/5']);
    });
    await nextTick();
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['PUT /api/v1/row-window-maps/5']);

    server.release();
    await waitFor(() => {
      expect(save.isConnected).toBe(false);
    });
    expect(server.sent).toEqual(['PUT /api/v1/row-window-maps/5']);
  });

  it('SourceEventMapModal: подвійний клік шле один PUT; після успіху діалог знято', async () => {
    const server = holdWrites({ read, write: () => ({ body: EventMap }) });

    function Host(): JSX.Element | null {
      const [open, setOpen] = useState(true);

      return open ? (
        <SourceEventMapModal dataSourceId={7} sourceEntityId={42} template="FLARE_EVENTS" map={EventMap} documents={[{ id: 100, businessKey: 'DOC-000123' } as never]} onClose={() => setOpen(false)} />
      ) : null;
    }

    renderKeepMounted(<Host />);
    const form = await screen.findByRole('dialog');
    const save = within(form).getByRole('button', { name: /common\.save/ });
    await waitFor(() => {
      expect(save.hasAttribute('disabled')).toBe(false);
    });

    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['PUT /api/v1/source-event-maps/12']);
    });
    await nextTick();
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['PUT /api/v1/source-event-maps/12']);

    server.release();
    await waitFor(() => {
      expect(save.isConnected).toBe(false);
    });
    expect(server.sent).toEqual(['PUT /api/v1/source-event-maps/12']);
  });
});
