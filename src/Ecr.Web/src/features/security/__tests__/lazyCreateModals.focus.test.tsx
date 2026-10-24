import { useState, type JSX, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Button, MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { LazyCreateRoleModal, LazyCreateUserModal } from '../lazyCreateModals';

/**
 * WCAG 2.4.3: «New role» / «New user» (`lazyCreateModals.tsx`, D-132) — натиснули РАНІШЕ, ніж приїхав чанк.
 *
 * ⛔ Діалог монтується ВЖЕ відкритим, `returnFocus` Mantine переходу `opened` false → true не бачить, і після закриття
 * фокус падав на `BODY`. Чанк тримає `vi.mock` із воротами — модуль віддається лише після кліку; кожен модуль
 * вантажиться у файлі раз, тож на модуль — один тест.
 *
 * Мутаційний доказ: прибрати `useRestoreFocus(props.opened)` з `LazyCreateRoleModal` — червоний перший тест;
 * з `LazyCreateUserModal` — другий.
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 */
const chunks = vi.hoisted(() => {
  const gate = (): { ready: Promise<void>; open: () => void } => {
    let release: () => void = () => undefined;
    const ready = new Promise<void>((resolve) => {
      release = resolve;
    });

    return { ready, open: () => release() };
  };

  return { role: gate(), user: gate() };
});

vi.mock('@/features/security/CreateRoleModal', async (importOriginal) => {
  await chunks.role.ready;

  return importOriginal();
});
vi.mock('@/features/security/CreateUserModal', async (importOriginal) => {
  await chunks.user.ready;

  return importOriginal();
});

function wrap(node: ReactNode): JSX.Element {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })),
  );

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        {node}
      </QueryClientProvider>
    </MantineProvider>
  );
}

async function openLateThenCancel(opener: HTMLElement, release: () => void): Promise<void> {
  opener.focus();
  fireEvent.click(opener);
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
  // Діалогу ще немає: чанк не приїхав, а `opened` уже `true`.
  expect(screen.queryByRole('dialog')).toBeNull();

  release();
  const dialog = await screen.findByRole('dialog');
  await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

  fireEvent.click(within(dialog).getByRole('button', { name: /common\.cancel/ }));
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  await waitFor(() => expect(document.activeElement).toBe(opener));
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('«New role» / «New user»: фокус після закриття, коли чанк запізнився', () => {
  it('«New role»: фокус повертається на кнопку', async () => {
    function Harness(): JSX.Element {
      const [opened, setOpened] = useState(false);

      return (
        <>
          <Button onClick={() => setOpened(true)}>New role</Button>
          <LazyCreateRoleModal
            opened={opened}
            onClose={() => setOpened(false)}
            permissions={[]}
            permissionCatalogError={null}
            onRetry={() => undefined}
          />
        </>
      );
    }
    render(wrap(<Harness />));

    await openLateThenCancel(screen.getByRole('button', { name: 'New role' }), chunks.role.open);
  });

  it('«New user»: фокус повертається на кнопку', async () => {
    function Harness(): JSX.Element {
      const [opened, setOpened] = useState(false);

      return (
        <>
          <Button onClick={() => setOpened(true)}>New user</Button>
          <LazyCreateUserModal
            opened={opened}
            onClose={() => setOpened(false)}
            roles={[]}
            rolesError={null}
            onRetry={() => undefined}
          />
        </>
      );
    }
    render(wrap(<Harness />));

    await openLateThenCancel(screen.getByRole('button', { name: 'New user' }), chunks.user.open);
  });
});
