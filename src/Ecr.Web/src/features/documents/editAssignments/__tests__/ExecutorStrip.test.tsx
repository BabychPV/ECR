import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ExecutorStrip } from '@/features/documents/editAssignments/ExecutorStrip';
import {
  fillEditAssignmentUrl,
  myEditAssignmentsUrl,
  sliceLabel,
  type MyEditAssignment,
} from '@/features/documents/editAssignments/editAssignmentsApi';
import { decide } from '@/features/grid/permissions';
import { testTheme } from '@/test/render';

/**
 * EA-5b: смуга виконавця. Мок API - за контрактом `DESIGN-OWNER-EDIT-GRANTS-2026-10-11.md` §6
 * (`GET /me/edit-assignments?role=assignee&documentId=`, `POST …/{id}/fill`); сервера не чекаємо.
 */

const DocumentId = 7;
const Period = 202601;

const january: MyEditAssignment = { id: 11, documentId: DocumentId, slice: { monthNumber: 1 }, status: 'Assigned' };

interface Calls {
  readonly fills: string[];
}

function mockApi(initial: readonly MyEditAssignment[], options: { fillStatus?: number } = {}): Calls {
  let items = [...initial];
  const calls: Calls = { fills: [] };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (url.endsWith(myEditAssignmentsUrl(DocumentId))) {
        return new Response(JSON.stringify({ items }), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }

      if (init?.method === 'POST' && url.endsWith(fillEditAssignmentUrl(DocumentId, 11))) {
        calls.fills.push(url);

        if (options.fillStatus !== undefined && options.fillStatus !== 200) {
          return new Response(
            JSON.stringify({ title: 'x', status: options.fillStatus, errorCode: 'ECR-ACCS-0404', correlationId: 'c' }),
            { status: options.fillStatus, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

        items = items.map((item) => (item.id === 11 ? { ...item, status: 'Filled' as const } : item));
        return new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }

      return new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  return calls;
}

function show(periodKind?: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ExecutorStrip documentId={DocumentId} periodKey={Period} periodKind={periodKind} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ExecutorStrip', () => {
  it('без передач смуги немає (власник і звичайний користувач)', async () => {
    const fetchSpy = mockApi([]);
    show();

    await waitFor(() => expect(globalThis.fetch).toHaveBeenCalled());
    expect(screen.queryByTestId('executor-strip')).toBeNull();
    expect(fetchSpy.fills).toEqual([]);
  });

  it('«Передано» - місяць, інструкція власника і кнопка «Заповнено»', async () => {
    mockApi([{ ...january, note: 'Due Friday <b>x</b>' }]);
    show();

    const strip = await screen.findByTestId('executor-strip');
    expect(strip.getAttribute('role')).toBe('status');
    expect(within(strip).getByTestId('executor-strip-text').textContent).toContain('editAssign.bar.assigned');
    expect(within(strip).getByTestId('executor-strip-text').textContent).toContain('slice=January');
    // Текст власника - звичайний текст: розмітка не виконується.
    expect(within(strip).getByTestId('executor-strip-note').textContent).toContain('Due Friday <b>x</b>');
    expect(strip.querySelector('b')).toBeNull();
    expect(within(strip).getByRole('button', { name: /editAssign\.bar\.markFilled/ })).toBeTruthy();
  });

  it('«Заповнено»: спершу підтвердження, потім POST fill; кнопка зникає, смуга каже «чекає валідації»', async () => {
    const calls = mockApi([january]);
    const user = userEvent.setup();
    show();

    await user.click(await screen.findByTestId('executor-strip-fill'));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.textContent).toContain('editAssign.bar.confirmFill');
    expect(calls.fills).toEqual([]);

    await user.click(within(dialog).getByTestId('executor-strip-fill-confirm'));

    await waitFor(() => expect(screen.getByTestId('executor-strip-row').getAttribute('data-status')).toBe('Filled'));
    expect(calls.fills).toEqual([`/api/v1/documents/${String(DocumentId)}/edit-assignments/11/fill`]);
    expect(screen.queryByTestId('executor-strip-fill')).toBeNull();
    expect(screen.getByTestId('executor-strip-text').textContent).toContain('editAssign.bar.filled');
    // Фокус не провалюється в body: його бере сама смуга.
    expect(document.activeElement).toBe(screen.getByTestId('executor-strip'));
  });

  it('«Скасувати» у підтвердженні нічого не надсилає', async () => {
    const calls = mockApi([january]);
    const user = userEvent.setup();
    show();

    await user.click(await screen.findByTestId('executor-strip-fill'));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /common\.cancel/ }));

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(calls.fills).toEqual([]);
    expect(screen.getByTestId('executor-strip-fill')).toBeTruthy();
  });

  it('Escape закриває підтвердження і повертає фокус на кнопку', async () => {
    mockApi([january]);
    const user = userEvent.setup();
    show();

    const button = await screen.findByTestId('executor-strip-fill');
    await user.click(button);
    await screen.findByRole('dialog');
    await user.keyboard('{Escape}');

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(button));
  });

  it('відмова сервера: смуга лишається в «Передано», кнопка на місці', async () => {
    const calls = mockApi([january], { fillStatus: 404 });
    const user = userEvent.setup();
    show();

    await user.click(await screen.findByTestId('executor-strip-fill'));
    await user.click(within(await screen.findByRole('dialog')).getByTestId('executor-strip-fill-confirm'));

    await waitFor(() => expect(calls.fills).toHaveLength(1));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(screen.getByTestId('executor-strip-row').getAttribute('data-status')).toBe('Assigned');
    expect(screen.getByTestId('executor-strip-fill')).toBeTruthy();
  });

  it('«Повернуто»: коментар власника видно, «Заповнено» знову доступне', async () => {
    mockApi([{ ...january, status: 'Returned', returnReason: 'Fix row 3 <i>now</i>' }]);
    show();

    const text = await screen.findByTestId('executor-strip-text');
    expect(text.textContent).toContain('editAssign.bar.returned');
    expect(text.textContent).toContain('Fix row 3 <i>now</i>');
    expect(text.querySelector('i')).toBeNull();
    expect(screen.getByTestId('executor-strip-fill')).toBeTruthy();
  });

  it('«Повернуто» без коментаря - окремий текст, а не порожній хвіст', async () => {
    mockApi([{ ...january, status: 'Returned', returnReason: '  ' }]);
    show();

    expect((await screen.findByTestId('executor-strip-text')).textContent).toContain('editAssign.bar.returnedNoReason');
  });

  it('«Заповнено» і «Валідовано» - без кнопки', async () => {
    mockApi([
      { ...january, id: 11, status: 'Filled' },
      { ...january, id: 12, slice: { monthNumber: 2 }, status: 'Filled', validationStatus: 'Validated' },
    ]);
    show();

    const rows = await screen.findAllByTestId('executor-strip-row');
    expect(rows).toHaveLength(2);
    expect(screen.queryByTestId('executor-strip-fill')).toBeNull();
    expect(rows[0]?.textContent).toContain('editAssign.bar.filled');
    expect(rows[1]?.textContent).toContain('editAssign.bar.validated');
  });
});

describe('sliceLabel', () => {
  it('місяць, період місячного проєкту, період іншого виду, аркуш/таблиця, увесь документ', () => {
    expect(sliceLabel({ monthNumber: 3 }, undefined)).toBe('March');
    expect(sliceLabel({ periodKey: 202601 }, 'Monthly')).not.toContain('editAssign.slice.period');
    // Квартал №1 не можна подати як «січень».
    expect(sliceLabel({ periodKey: 202601 }, 'Quarterly')).toContain('editAssign.slice.period');
    expect(sliceLabel({ sheetCode: 'F1', tableCode: 'T2' }, undefined)).toBe('F1 / T2');
    expect(sliceLabel({}, undefined)).toContain('editAssign.slice.all');
  });
});

describe('причини заборони передачі місяця', () => {
  it.each(['LockedByOwner', 'AssignmentFilled', 'AssignmentValidated'] as const)('%s має власну підказку', (reason) => {
    const decision = decide(
      { cellPermissions: { 'R1:C1': reason } } as never,
      'R1',
      { code: 'C1', isReadOnly: false } as never,
    );

    expect(decision.editable).toBe(false);
    expect(decision.reason).toBe(reason);
    expect(decision.hint).toContain(`deny.${reason}`);
  });
});
