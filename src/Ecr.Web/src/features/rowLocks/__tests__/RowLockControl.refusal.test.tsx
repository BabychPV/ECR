import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import type { RowLockDto } from '@/features/documents/rowLocks/api';
import { publishFocus, resetFocus } from '@/features/grid/focusStore';
import { showApiError } from '@/shared/ui/notify';
import { testTheme } from '@/test/render';
import { RowLockControl } from '../RowLockControl';

/**
 * ✎ B139 (F13, пункт b): відмова сервера на блокування (`POST …/row-locks` → 403/409) і на зняття
 * (`DELETE …/row-locks/{id}` → 403) — людина бачить причину (`showApiError`), діалог лишається відкритим із
 * введеним, кнопкою можна скористатися знову; перелік тримає рядок блокування, «Зняти» знову доступна.
 *
 * ⛔ Мутації (`useRowLocks.ts`): прибрати `onError` в `useCreateRowLocks` — червоніють обидва тести діалогу
 * («showApiError не викликано»); прибрати `onError` в `useReleaseRowLock` — червоніє тест переліку.
 */
vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showApiError: vi.fn(),
}));

// ⚠ Діалог і перелік — `lazy()`-чанки (тягнуть `@mantine/dates`): прогріти до тестів, щоб перше відкриття не
// залежало від швидкості імпорту під навантаженням (та сама причина, що в `RowLockControl.test.tsx`).
beforeAll(async () => {
  await Promise.all([import('../RowLockDialog'), import('../RowLocksPanel')]);
}, 120_000);

const DocumentId = 96;
const PeriodKey = 202611;
const TableInstanceId = 501;
const Table = { sheetCode: 'S1', tableCode: 'T1', sheetCopyId: null };

const Slice = {
  columns: [],
  rows: [
    { rowKey: 'R1', label: 'Boiler 1' },
    { rowKey: 'R2', label: 'Boiler 2' },
  ],
} as unknown as TableSliceDto;

const Lock: RowLockDto = {
  canUnlock: true,
  id: 5,
  isActive: true,
  lockFrom: '2026-11-01',
  lockUntil: '2026-11-30',
  note: 'audit',
  ownerDisplayName: 'A. Serikbayev',
  ownerUserId: 3,
  rowKey: 'R2',
  sheetCode: 'S1',
  sheetCopyId: null,
  tableCode: 'T1',
};

let sent: { method: string; url: string }[] = [];

function show(refusal: { status: number; messageKey: string }, locks: RowLockDto[] = []): void {
  sent = [];
  vi.mocked(showApiError).mockClear();
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = String(init?.method ?? 'GET');
      const json = (body: unknown, status = 200, type = 'application/json'): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': type } });

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions: ['Document.View', 'Document.RowLock'],
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }
      if (url.includes('/row-locks') && method !== 'GET') {
        sent.push({ method, url });

        return json(
          {
            title: 'Refused',
            status: refusal.status,
            errorCode: refusal.status === 403 ? 'ECR-ACCS-0403' : 'ECR-ROW-0409',
            messageKey: refusal.messageKey,
            correlationId: 'c1',
          },
          refusal.status,
          'application/problem+json',
        );
      }

      return json([]);
    }),
  );

  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RowLockControl
          documentId={DocumentId}
          periodKey={PeriodKey}
          tableInstanceId={TableInstanceId}
          table={Table}
          slice={Slice}
          locks={locks}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const usable = (button: HTMLElement): boolean =>
  !(button as HTMLButtonElement).disabled && !button.hasAttribute('data-loading');

afterEach(() => {
  resetFocus();
  vi.unstubAllGlobals();
});

describe('RowLockControl: відмова сервера (B139, F13 b)', () => {
  it.each([
    { status: 403, messageKey: 'err.ECR-ACCS-0403.rowLockDenied' },
    { status: 409, messageKey: 'err.ECR-ROW-0409.rowAlreadyLocked' },
  ])('POST → $status: причина показана, діалог відкритий із введеним, кнопка знову доступна', async (refusal) => {
    show(refusal);
    act(() => publishFocus(TableInstanceId, PeriodKey, { rowIndex: 0, columnCode: 'A' } as never));

    const open = await screen.findByRole('button', { name: /rowLock\.lockAction/ });
    await waitFor(() => expect((open as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(open);

    const dialog = await screen.findByRole('dialog');
    const save = await screen.findByRole('button', { name: /rowLock\.lockVerb/ });
    const [from, to] = Array.from(dialog.querySelectorAll('input')).filter((input) => input.type !== 'hidden');
    const note = dialog.querySelector('textarea');
    if (from === undefined || to === undefined || note === null) throw new Error('полів форми немає');
    fireEvent.change(from, { target: { value: '2026-11-10' } });
    fireEvent.blur(from);
    fireEvent.change(to, { target: { value: '2026-11-20' } });
    fireEvent.blur(to);
    fireEvent.change(note, { target: { value: 'Annual audit' } });

    await waitFor(() => expect((save as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(save);

    await waitFor(() => expect(sent).toHaveLength(1));
    await waitFor(() => expect(vi.mocked(showApiError)).toHaveBeenCalledTimes(1));
    const shown = vi.mocked(showApiError).mock.calls[0]?.[0] as { problem?: { status?: number } } | undefined;
    expect(shown?.problem?.status).toBe(refusal.status);

    // Діалог не закрився, введене на місці.
    expect(screen.getByRole('dialog')).toBe(dialog);
    expect(dialog.textContent).toContain('Boiler 1');
    expect(from.value).toBe('2026-11-10');
    expect(to.value).toBe('2026-11-20');
    expect(note.value).toBe('Annual audit');

    // Кнопкою можна скористатися знову: другий запит іде.
    await waitFor(() => expect(usable(save)).toBe(true));
    fireEvent.click(save);
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent.every((request) => request.method === 'POST')).toBe(true);
  });

  it('DELETE → 403: причина показана, рядок блокування в переліку лишився, «Зняти» знову доступна', async () => {
    show({ status: 403, messageKey: 'err.ECR-ACCS-0403.rowLockDenied' }, [Lock]);

    fireEvent.click(await screen.findByRole('button', { name: /rowLock\.listAction/ }));
    const release = await screen.findByRole('button', { name: /rowLock\.release/ });
    fireEvent.click(release);

    await waitFor(() => expect(sent).toHaveLength(1));
    expect(sent[0]?.method).toBe('DELETE');
    await waitFor(() => expect(vi.mocked(showApiError)).toHaveBeenCalledTimes(1));
    const shown = vi.mocked(showApiError).mock.calls[0]?.[0] as { problem?: { status?: number } } | undefined;
    expect(shown?.problem?.status).toBe(403);

    // Перелік відкритий, рядок блокування на місці.
    expect(screen.getByRole('dialog').querySelector('[data-row-lock-item="5"]')).not.toBeNull();
    expect(screen.getByText('Boiler 2')).toBeTruthy();

    const again = screen.getByRole('button', { name: /rowLock\.release/ });
    await waitFor(() => expect(usable(again)).toBe(true));
    fireEvent.click(again);
    await waitFor(() => expect(sent).toHaveLength(2));
    expect(sent[1]?.url).toMatch(/\/row-locks\/5$/);
  });
});
