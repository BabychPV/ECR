import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { TableSliceDto } from '@/api/types';
import { renderWithQuery } from '@/test/render';
import { DeleteRowControl } from '../DeleteRowControl';
import { publishFocus, resetFocus } from '../focusStore';
import { isDeletableRow } from '../useDeleteRow';

/**
 * D3-A (клієнт): «Видалити рядок» діє лише на рядок, доданий користувачем, і лише після підтвердження.
 *
 * ⛔ Мутаційні докази: прибери `ConfirmModal` (виклич `mutate` одразу в `onClick`) — падає «жодного запиту до
 * підтвердження»; зроби `isDeletableRow` безумовним `true` — падає «рядок шаблону не видаляється».
 */
const showApiError = vi.hoisted(() => vi.fn());
const followRecalculation = vi.hoisted(() => vi.fn());
vi.mock('@/shared/ui/notify', () => ({ showApiError, showWarning: vi.fn() }));
vi.mock('../useCellPatch', () => ({ followRecalculation }));

const DocumentId = 5;
const TableInstanceId = 9;
const PeriodKey = 202601;

const slice = {
  rows: [
    { rowKey: 'tpl', ordinal: 1, rowKind: 'Item', label: 'Template row', rowVersion: 'v', cells: {}, isOrphaned: false },
    { rowKey: 'dyn1', ordinal: 2, rowKind: 'Dynamic', label: null, rowVersion: 'v', cells: {}, isOrphaned: false },
  ],
} as unknown as TableSliceDto;

function json(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

describe('isDeletableRow', () => {
  it('лише рядок без опису в шаблоні (rowKind = режим таблиці)', () => {
    expect(isDeletableRow({ rowKind: 'Dynamic' })).toBe(true);
    expect(isDeletableRow({ rowKind: 'Mixed' })).toBe(true);
    expect(isDeletableRow({ rowKind: 'Item' })).toBe(false);
    expect(isDeletableRow({ rowKind: 'Group' })).toBe(false);
    expect(isDeletableRow(undefined)).toBe(false);
  });
});

describe('DeleteRowControl', () => {
  const fetchMock = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>();
  const onDeleted = vi.fn();

  function mount(): void {
    renderWithQuery(
      <DeleteRowControl
        documentId={DocumentId}
        tableInstanceId={TableInstanceId}
        periodKey={PeriodKey}
        slice={slice}
        onDeleted={onDeleted}
      />,
    );
  }

  beforeEach(() => {
    fetchMock.mockReset();
    onDeleted.mockReset();
    showApiError.mockReset();
    followRecalculation.mockReset();
    resetFocus();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it('без фокуса на рядку кнопка вимкнена', () => {
    mount();

    expect(screen.getByRole('button', { name: /grid\.deleteRow\b/ }).hasAttribute('disabled')).toBe(true);
  });

  it('вимкнена кнопка має пояснення не лише в title: прихований текст за aria-describedby', () => {
    mount();

    const button = screen.getByRole('button', { name: /grid\.deleteRow\b/ });
    const id = button.getAttribute('aria-describedby');
    expect(id).toBeTruthy();
    expect(document.getElementById(id!)?.textContent).toContain('grid.deleteRowHint');
  });

  it('рядок шаблону не видаляється: кнопка вимкнена', () => {
    publishFocus(TableInstanceId, PeriodKey, { rowIndex: 0, columnIndex: 0 });
    mount();

    expect(screen.getByRole('button', { name: /grid\.deleteRow\b/ }).hasAttribute('disabled')).toBe(true);
  });

  it('жодного запиту до підтвердження; після нього — DELETE за ключем рядка і стеження за перерахунком', async () => {
    fetchMock.mockResolvedValue(json({ recalculationJobId: 'job-1' }, 200));
    publishFocus(TableInstanceId, PeriodKey, { rowIndex: 1, columnIndex: 0 });
    mount();
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /grid\.deleteRow\b/ }));

    // Діалог відкрито, запиту ще немає; без підпису рядка в назві — ключ рядка.
    await screen.findByTestId('confirm-verb');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.getByText(/grid\.deleteRowTitle/)).toBeTruthy();

    await user.click(screen.getByTestId('confirm-verb'));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(String(url)).toBe(`/api/v1/documents/${DocumentId}/tables/${TableInstanceId}/rows/dyn1`);
    expect(init?.method).toBe('DELETE');

    await waitFor(() => expect(onDeleted).toHaveBeenCalledTimes(1));
    expect(followRecalculation).toHaveBeenCalledWith(expect.anything(), 'job-1', DocumentId, PeriodKey);
    expect(showApiError).not.toHaveBeenCalled();
  });

  it('скасування не робить запиту', async () => {
    publishFocus(TableInstanceId, PeriodKey, { rowIndex: 1, columnIndex: 0 });
    mount();
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /grid\.deleteRow\b/ }));
    await user.click(await screen.findByTestId('confirm-cancel'));

    expect(fetchMock).not.toHaveBeenCalled();
    expect(onDeleted).not.toHaveBeenCalled();
  });

  it('відмова сервера показується й сітку не перечитує', async () => {
    fetchMock.mockResolvedValue(
      json(
        {
          title: 'Row',
          status: 409,
          errorCode: 'ECR-ROW-0409',
          messageKey: 'err.ECR-ROW-0409.templateRowNotDeletable',
        },
        409,
      ),
    );
    publishFocus(TableInstanceId, PeriodKey, { rowIndex: 1, columnIndex: 0 });
    mount();
    const user = userEvent.setup();

    await user.click(screen.getByRole('button', { name: /grid\.deleteRow\b/ }));
    await user.click(await screen.findByTestId('confirm-verb'));

    await waitFor(() => expect(showApiError).toHaveBeenCalledTimes(1));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => {
      await Promise.resolve();
    });
    expect(onDeleted).not.toHaveBeenCalled();
  });
});
