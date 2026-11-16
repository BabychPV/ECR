import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { SheetCopyBar, type SheetCopyTarget } from '../SheetCopyBar';

/**
 * B145: діалог видалення копії аркуша, що вже закривається, не шле `DELETE` вдруге.
 *
 * ⚠ Після відповіді сервера (успіх чи відмова) `SheetCopyBar` закриває діалог і знімає зайнятість, а кнопка
 * діалогу ще в DOM на час анімації — другий клік подвійного кліку видаляв би вдруге. Захист — в обгортці
 * `LazyConfirmModal` (`features/workflow/lazyDialogs.tsx`); без нього тут два `DELETE`.
 *
 * ⚠ Без залежності від часу: діалог підмінено кнопкою, яка є в DOM завжди, відповідь сервера віддає тест.
 */
vi.mock('@/shared/ui/ConfirmModal', async () => ({
  ConfirmModal: (await import('@/test/alwaysMountedDialogs')).ConfirmModal,
}));

const Copy: SheetCopyTarget = {
  sheetDefId: 10,
  sheetCode: 'GEN',
  label: 'Boiler 2',
  groupNameValues: { en: 'Boiler' },
  sheetCopyId: 77,
  isCopyable: true,
};

const sent: string[] = [];
let respond: (() => void) | null = null;

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string, init?: RequestInit) => {
      const method = String(init?.method ?? 'GET');
      if (method !== 'DELETE') {
        return new Response(JSON.stringify([]), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }

      sent.push(`DELETE ${String(input)}`);
      await new Promise<void>((resolve) => {
        respond = resolve;
      });

      return new Response(null, { status: 204 });
    }),
  );
}

afterEach(() => {
  sent.length = 0;
  respond = null;
  vi.unstubAllGlobals();
});

describe('B145: видалення копії аркуша — діалог, що закривається', () => {
  it('клік по «Delete copy» діалогу після відповіді сервера не шле другого DELETE', async () => {
    mockServer();
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SheetCopyBar documentId={42} target={Copy} canAdd onSelect={() => undefined} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(screen.getByTestId('sheet-copy-delete'));
    const confirm = await screen.findByRole('button', { name: 'confirm:⟦documents.sheetCopies.delete⟧' });
    expect(confirm.getAttribute('data-opened')).toBe('true');

    fireEvent.click(confirm);
    await waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    expect(sent[0]).toMatch(/^DELETE \/api\/v1\/documents\/42\/.*77$/);

    // Сервер відповів: діалог закрито, запит завершено.
    respond?.();
    await waitFor(() => {
      expect(confirm.getAttribute('data-opened')).toBe('false');
      expect(confirm.getAttribute('data-pending')).toBe('false');
    });

    // Клік по кнопці діалогу, що закривається.
    fireEvent.click(confirm);

    // Незахищена мутація стартує одразу: кілька тіків циклу подій — і другий DELETE вже був би в журналі.
    await new Promise<void>((resolve) => setImmediate(resolve));
    await new Promise<void>((resolve) => setImmediate(resolve));
    expect(sent).toHaveLength(1);
  });
});
