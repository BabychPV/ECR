import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { DocumentTableDto } from '@/api/types';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { permitHistoryKey, type DocumentPermitHistory } from '../api';
import { CellSplitDialog } from '../CellSplitDialog';
import { HeaderSplitBanner } from '../HeaderSplitBanner';
import { permitGridMarksOf } from '../splitModel';
import { historyOf, seaWaterRow } from './historyFixture';

/**
 * B149: «Split February 2026 — …» (одна комірка й «Split all» шапки) — один `PUT …/permit-history/splits` на збереження.
 *
 * Зонд (база 826f654e4):
 * - закриття — ЗАКРИТО: власники знімають діалоги з дерева (`DocumentInspector`: `{splitRequest !== null && …}`,
 *   `HeaderSplitBanner`: `{splitting !== null && …}`), а самі діалоги — `<Modal opened>` без закритого стану;
 * - подвійний клік — ВІДКРИТО: `submit` не дивився на `isPending`, спінер — лише через 100 мс (`usePendingLoading`), тож
 *   другий сабміт (наступний тік) слав другий `PUT /api/v1/documents/3/permit-history/splits` з тим самим тілом.
 *
 * Мутаційні докази: прибрати `save.isPending ||` / `if (save.isPending) return;` у `CellSplitDialog.tsx` /
 * `HeaderSplitDialog.tsx` → червоний відповідний тест (`expected [ …(2) ] to deeply equal [ Array(1) ]`).
 */

const Save = 'PUT /api/v1/documents/3/permit-history/splits';

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: CellSplitDialog — один PUT на збереження', () => {
  it('подвійний сабміт шле один PUT; після успіху діалог знято з дерева', async () => {
    const history = historyOf(seaWaterRow());
    const server = holdWrites({ read: () => history, write: () => ({ body: history }) });
    const mark = permitGridMarksOf(history, 701, null).cells.get('R1:Feb') ?? null;
    const target = { documentId: 3, periodKey: 202602, tableInstanceId: 41, tableDefId: 701, sheetCopyId: null, rowKey: 'R1', columnCode: 'Feb' };

    // Власник (`DocumentInspector`) монтує діалог лише за запитом і знімає його на закритті.
    function Host(): JSX.Element | null {
      const [open, setOpen] = useState(true);

      return open ? (
        <CellSplitDialog target={target} mark={mark} historyPending={false} rowLabel="Sea Water Intake" unit="t" baseVersion="AAAB" onClose={() => setOpen(false)} />
      ) : null;
    }

    renderKeepMounted(<Host />);
    const dialog = screen.getByRole('dialog');
    const [a, b] = within(dialog).getAllByRole('textbox');
    fireEvent.change(a!, { target: { value: '5.8' } });
    fireEvent.change(b!, { target: { value: '6.6' } });
    const form = dialog.querySelector('form');
    if (form === null) throw new Error('форми немає');

    fireEvent.submit(form);
    await waitFor(() => {
      expect(server.sent).toEqual([Save]);
    });
    await nextTick();
    fireEvent.submit(form);
    await settle();
    expect(server.sent).toEqual([Save]);

    server.release();
    await waitFor(() => {
      expect(form.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([Save]);
  });
});

/** A row of «Air emissions» relying on the header permit; February holds `total`, not split. */
function airRow(rowKey: string, total: string) {
  const month = seaWaterRow().splitMonths[0]!;

  return seaWaterRow({
    tableDefId: 801,
    tableCode: 'AIR',
    rowKey,
    columnCode: 'PermitNo',
    permitSource: 'header',
    splitMonths: [{ ...month, columnCodes: ['Feb'], cells: [{ columnCode: 'Feb', total, parts: [null, null], isSplit: false }] }],
  });
}

describe('B149: HeaderSplitDialog — один PUT на збереження', () => {
  it('подвійний сабміт шле один PUT; після успіху діалог знято з дерева (власник — HeaderSplitBanner)', async () => {
    const history: DocumentPermitHistory = {
      header: [{ fieldCode: 'PermitNo', permits: seaWaterRow().permits }],
      rows: [airRow('SO2', '7.3'), airRow('NO2', '26.7')],
      cellsNeedingSplit: 2,
    };
    const tables = [
      { tableDefId: 801, sheetCopyId: null, tableInstanceId: 51, tableCode: 'AIR', tableNameL10n: { values: { en: 'Air emissions' } } },
    ] as unknown as DocumentTableDto[];
    const slice = {
      tableInstanceId: 51,
      periodKey: 202602,
      columns: [{ code: 'Feb', dataType: 'Decimal', unitSymbol: 't' }],
      rows: [
        { rowKey: 'NO2', label: 'Nitrogen dioxide', rowVersion: 'V1', cells: {} },
        { rowKey: 'SO2', label: 'Sulphur dioxide', rowVersion: 'V2', cells: {} },
      ],
    };
    const server = holdWrites({
      read: (url) => (url.includes('/tables/') ? slice : history),
      write: () => ({ body: history }),
    });

    function Seeded(): JSX.Element {
      const client = useQueryClient();
      useState(() => {
        client.setQueryData(permitHistoryKey(3, 202602), history);
        client.setQueryData(queryKeys.slices.one(51, 202602), slice);

        return true;
      });

      return <HeaderSplitBanner documentId={3} periodKey={202602} tables={tables} readOnly={false} />;
    }

    renderKeepMounted(<Seeded />);

    fireEvent.click(await screen.findByRole('button', { name: /permits\.header\.splitAll/ }));
    const dialog = screen.getByRole('dialog');
    fireEvent.change(within(dialog).getByRole('textbox', { name: /Sulphur dioxide.*A/ }), { target: { value: '3' } });
    const form = dialog.querySelector('form');
    if (form === null) throw new Error('форми немає');

    fireEvent.submit(form);
    await waitFor(() => {
      expect(server.sent).toEqual([Save]);
    });
    await nextTick();
    fireEvent.submit(form);
    await settle();
    expect(server.sent).toEqual([Save]);

    server.release();
    await waitFor(() => {
      expect(form.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([Save]);
  });
});
