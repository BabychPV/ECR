import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { QueryClientProvider } from '@tanstack/react-query';
import { createQueryClient } from '@/app/queryClient';
import { ExecutorStrip } from '@/features/editAssignments/ExecutorStrip';
import type { MyEditAssignment } from '@/features/editAssignments/types';
import { testTheme } from '@/test/render';

/**
 * EA-5b: смуга виконавця. Мок API - за контрактом `DESIGN-OWNER-EDIT-GRANTS-2026-10-11.md` §6:
 * `GET /me/edit-assignments` БЕЗ параметрів -> `{assignedToMe, awaitingMyValidation}` (смуга сама вибирає свій документ),
 * `POST …/{id}/fill`; сервера не чекаємо.
 */

const DocumentId = 7;
const Period = 202601;

const january: MyEditAssignment = {
  assignmentId: 11,
  documentId: DocumentId,
  documentKey: 'AIR-2026-01',
  slice: 'January',
  status: 'Assigned',
};

interface Calls {
  readonly fills: string[];
  readonly gets: string[];
}

function mockApi(
  initial: readonly MyEditAssignment[],
  options: { fillStatus?: number; listStatus?: number; simulation?: boolean } = {},
): Calls {
  let items = [...initial];
  const calls: Calls = { fills: [], gets: [] };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);

      if (options.simulation !== undefined && url.endsWith('/api/v1/me')) {
        return new Response(
          JSON.stringify({
            denies: [], grants: {}, isSimulation: options.simulation, language: 'en', mustChangePassword: false,
            permissions: ['Document.View'], simulatedForUserId: null, userId: 1, userName: 'executor',
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }

      if (url.endsWith('/api/v1/me/edit-assignments')) {
        calls.gets.push(url);

        if (options.listStatus !== undefined && options.listStatus !== 200) {
          return new Response(
            JSON.stringify({ title: 'x', status: options.listStatus, errorCode: 'ECR-GEN-0404', correlationId: 'c' }),
            { status: options.listStatus, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

        return new Response(JSON.stringify({ assignedToMe: items, awaitingMyValidation: [] }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      if (init?.method === 'POST' && url.endsWith(`/api/v1/documents/${String(DocumentId)}/edit-assignments/11/fill`)) {
        calls.fills.push(url);

        if (options.fillStatus !== undefined && options.fillStatus !== 200) {
          return new Response(
            JSON.stringify({ title: 'x', status: options.fillStatus, errorCode: 'ECR-ACCS-0404', correlationId: 'c' }),
            { status: options.fillStatus, headers: { 'Content-Type': 'application/problem+json' } },
          );
        }

        items = items.map((item) => (item.assignmentId === 11 ? { ...item, status: 'Filled' as const } : item));
        return new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } });
      }

      return new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  return calls;
}

function show(): void {
  // Справжня сітка застосунку: без `meta.handled` відмова «Заповнено» дала б ще й другий (глобальний) тост.
  const client = createQueryClient();
  client.setDefaultOptions({ queries: { retry: false } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <ExecutorStrip documentId={DocumentId} periodKey={Period} />
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
    const shown = vi.spyOn(notifications, 'show');
    const calls = mockApi([january], { fillStatus: 404 });
    const user = userEvent.setup();
    show();

    await user.click(await screen.findByTestId('executor-strip-fill'));
    await user.click(within(await screen.findByRole('dialog')).getByTestId('executor-strip-fill-confirm'));

    await waitFor(() => expect(calls.fills).toHaveLength(1));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    // Один тост, а не два: виклик показує помилку сам, сітка мовчить (meta.handled).
    expect(shown).toHaveBeenCalledTimes(1);
    shown.mockRestore();
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

  it('у симуляції текст смуги є, а кнопки «Заповнено» немає і fill не викликається', async () => {
    const calls = mockApi([january], { simulation: true });
    show();

    const strip = await screen.findByTestId('executor-strip');
    await waitFor(() => expect(screen.queryByTestId('executor-strip-fill')).toBeNull());
    expect(within(strip).getByTestId('executor-strip-text').textContent).toContain('editAssign.bar.assigned');
    expect(calls.fills).toEqual([]);
  });

  it('«Заповнено» і «Валідовано» - без кнопки', async () => {
    mockApi([{ ...january, status: 'Filled' }]);
    show();

    const rows = await screen.findAllByTestId('executor-strip-row');
    expect(rows).toHaveLength(1);
    expect(screen.queryByTestId('executor-strip-fill')).toBeNull();
    expect(rows[0]?.textContent).toContain('editAssign.bar.filled');
  });

  it('передачі ІНШИХ документів у смузі не показуються (фільтр на клієнті)', async () => {
    const calls = mockApi([{ ...january, assignmentId: 99, documentId: 8, slice: 'February' }]);
    show();

    await waitFor(() => expect(calls.gets).toHaveLength(1));
    expect(screen.queryByTestId('executor-strip')).toBeNull();
  });

  it('404 від сервера (передач ще немає) - тиха порожня смуга, без помилок', async () => {
    const shown = vi.spyOn(notifications, 'show');
    const calls = mockApi([], { listStatus: 404 });
    show();

    await waitFor(() => expect(calls.gets).toHaveLength(1));
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(screen.queryByTestId('executor-strip')).toBeNull();
    expect(shown).not.toHaveBeenCalled();
    shown.mockRestore();
  });
});

