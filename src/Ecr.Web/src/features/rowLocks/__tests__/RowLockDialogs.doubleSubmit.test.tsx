import type { JSX } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import type { TableSliceDto } from '@/api/types';
import type { RowLockDto } from '@/features/documents/rowLocks/api';
import { publishFocus, resetFocus } from '@/features/grid/focusStore';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { RowLockControl } from '../RowLockControl';
import { useRowLocks } from '../useRowLocks';

// ⚠ Спінер (`usePendingLoading`) вмикається за 100 мс РЕАЛЬНОГО часу й вимикає кнопку: під навантаженням CPU тест
// доходив би до другого кліку вже після нього й був би зеленим і без захисту в обробнику. Подвійний клік людини
// швидший за 100 мс, тож тут спінера немає — доказ лише про обробник.
vi.mock('@/features/common/usePendingLoading', () => ({ usePendingLoading: () => false }));

/**
 * B149: «Lock rows…» і перелік блокувань — один запит на дію (зонд: ЗАКРИТО, база 826f654e4).
 *
 * Власник (`RowLockControl`) монтує діалог і перелік лише відкритими (`{open === 'lock' && …}`), самі вони —
 * `<Modal opened>` без закритого стану. Обидва обробники вже звіряють `isPending`, тож другий клік подвійного кліку
 * (наступний тік) нічого не шле; діалог закривається в `onSuccess` до перечитування, а перелік чекає перечитування
 * блокувань у тому самому запиті, тож знятого рядка з активною кнопкою не лишається.
 *
 * Тест тримає це: без `create.isPending` / `release.isPending` в обробниках — червоний відповідний тест.
 */

const DocumentId = 42;
const PeriodKey = 202611;
const TableInstanceId = 501;
const Table = { sheetCode: 'S1', tableCode: 'T1', sheetCopyId: null };
const Slice = { columns: [], rows: [{ rowKey: 'R1', label: 'Boiler 1' }, { rowKey: 'R2', label: 'Boiler 2' }] } as unknown as TableSliceDto;

const Lock: RowLockDto = {
  canUnlock: true, id: 5, isActive: true, lockFrom: '2026-11-01', lockUntil: '2026-11-30', note: null, ownerDisplayName: 'A. S.',
  ownerUserId: 3, rowKey: 'R2', sheetCode: 'S1', sheetCopyId: null, tableCode: 'T1',
};

const Me = { denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: ['Document.View', 'Document.RowLock'], simulatedForUserId: null, userId: 9, userName: 't' };

/** Власник як у сітці документа: перелік блокувань — із того самого запиту, що його перечитує дія. */
function Owner(): JSX.Element | null {
  const locks = useRowLocks(DocumentId, PeriodKey);

  return <RowLockControl documentId={DocumentId} periodKey={PeriodKey} tableInstanceId={TableInstanceId} table={Table} slice={Slice} locks={locks.data ?? []} />;
}

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

beforeAll(async () => {
  await Promise.all([import('../RowLockDialog'), import('../RowLocksPanel')]);
}, 120_000);

afterEach(() => {
  resetFocus();
  vi.unstubAllGlobals();
});

describe('B149: блокування рядків — один запит на дію', () => {
  it('Lock rows: подвійний клік шле один POST; після успіху діалог знято з дерева', async () => {
    const server = holdWrites({ read: (url) => (url.endsWith('/api/v1/me') ? Me : []), write: () => ({ status: 201, body: [Lock] }) });
    renderKeepMounted(<Owner />);
    act(() => publishFocus(TableInstanceId, PeriodKey, { rowIndex: 0, columnCode: 'A' } as never));

    const open = await screen.findByRole('button', { name: /rowLock\.lockAction/ });
    await waitFor(() => {
      expect((open as HTMLButtonElement).disabled).toBe(false);
    });
    fireEvent.click(open);
    const dialog = await screen.findByRole('dialog');
    const [from, to] = Array.from(dialog.querySelectorAll('input')).filter((input) => input.type !== 'hidden');
    if (from === undefined || to === undefined) throw new Error('полів дат немає');
    fireEvent.change(from, { target: { value: '2026-11-20' } });
    fireEvent.blur(from);
    fireEvent.change(to, { target: { value: '2026-11-25' } });
    fireEvent.blur(to);
    const save = within(dialog).getByRole('button', { name: /rowLock\.lockVerb/ });
    await waitFor(() => {
      expect((save as HTMLButtonElement).disabled).toBe(false);
    });

    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual([`POST /api/v1/documents/${String(DocumentId)}/row-locks`]);
    });
    await nextTick();
    fireEvent.click(save);
    await settle();
    expect(server.sent).toHaveLength(1);

    server.release();
    await waitFor(() => {
      expect(save.isConnected).toBe(false);
    });
    expect(server.sent).toHaveLength(1);
  });

  it('перелік: подвійний клік «Release» шле один DELETE; знятого рядка з кнопкою не лишається', async () => {
    let released = false;
    const server = holdWrites({
      read: (url) => {
        if (url.endsWith('/api/v1/me')) return Me;
        return url.includes('/row-locks') && !released ? [Lock] : [];
      },
      write: () => {
        released = true;
        return { status: 204 };
      },
    });
    renderKeepMounted(<Owner />);

    fireEvent.click(await screen.findByRole('button', { name: /rowLock\.listAction/ }));
    const release = await screen.findByRole('button', { name: /rowLock\.releaseRow/ });

    fireEvent.click(release);
    await waitFor(() => {
      expect(server.sent).toEqual([`DELETE /api/v1/documents/${String(DocumentId)}/row-locks/5`]);
    });
    await nextTick();
    fireEvent.click(release);
    await settle();
    expect(server.sent).toHaveLength(1);

    server.release();
    await waitFor(() => {
      expect(release.isConnected).toBe(false);
    });
    expect(server.sent).toHaveLength(1);
  });
});
