import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { isSessionClosed, LOGOUT_PATH, onBeforeLoginRedirect, resetSignOutForTests } from '@/api/client';
import { UserMenu } from '@/shared/ui/UserMenu';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';

/**
 * F6-01: «Вийти» мовчки знищував незбережені правки. Після `beginSignOut()`
 * мережа вкладки закрита, а `beforeunload` за закритого сеансу не шле маячка й
 * не питає — довезти набране потім нічим. Тепер: спершу зберегти; не вдалося —
 * діалог, і без явного «Вийти» людина лишається з даними.
 *
 * ⚠ jsdom не вміє `location.replace` (лише пише «not implemented»), тож вихід
 * видно за `POST /logout` і `isSessionClosed()`.
 */
const calls: string[] = [];

function server(): void {
  calls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      calls.push(`${init?.method ?? 'GET'} ${String(input)}`);

      return new Response(null, { status: 204 });
    }),
  );
}

function show(): void {
  render(
    <MantineProvider>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <UserMenu userName="tester" changePasswordPath="/change-password" settleTimeoutMs={50} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function clickSignOut(): Promise<ReturnType<typeof userEvent.setup>> {
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: 'tester' }));
  await user.click(await screen.findByRole('menuitem', { name: '⟦profile.logout⟧' }));

  return user;
}

const loggedOut = (): boolean => calls.some((call) => call === `POST ${LOGOUT_PATH}`);

const offs: (() => void)[] = [];

afterEach(() => {
  for (const off of offs.splice(0)) off();
  vi.unstubAllGlobals();
  resetSignOutForTests();
});

describe('UserMenu: вихід з незбереженими правками (F6-01)', () => {
  it('зберегти не вдалося — вихід не стається, показано діалог; «Залишитися» лишає сеанс', async () => {
    server();
    const flush = vi.fn(async () => false);
    offs.push(registerUnsavedSource('test-failed', { hasUnsaved: () => true, unsavedCount: () => 2, flush }));
    show();

    const user = await clickSignOut();

    // ⛔ Мутація: викликати `signOut` напряму (як було) — тут POST /logout і закритий сеанс.
    expect(await screen.findByTestId('unsaved-stay')).toBeDefined();
    expect(flush).toHaveBeenCalledTimes(1);
    expect(loggedOut()).toBe(false);
    expect(isSessionClosed()).toBe(false);

    await user.click(screen.getByTestId('unsaved-stay'));
    await waitFor(() => expect(screen.queryByTestId('unsaved-stay')).toBeNull());
    expect(loggedOut()).toBe(false);
    expect(isSessionClosed()).toBe(false);
  });

  it('«Вийти» в діалозі — вихід, і перед ним лишено слід lostEdits', async () => {
    server();
    offs.push(registerUnsavedSource('test-failed', { hasUnsaved: () => true, flush: async () => false }));
    const trail = vi.fn((from: string) => {
      // Слід пишеться, доки мережа ще відкрита.
      expect(isSessionClosed()).toBe(false);
      expect(from.length).toBeGreaterThan(0);
    });
    offs.push(onBeforeLoginRedirect(trail));
    show();

    const user = await clickSignOut();
    await user.click(await screen.findByTestId('unsaved-leave'));

    await waitFor(() => expect(loggedOut()).toBe(true));
    // ⛔ Мутація: прибрати `recordBeforeSignOut()` у `signOut` — слід не пишеться.
    expect(trail).toHaveBeenCalledTimes(1);
    expect(isSessionClosed()).toBe(true);
  });

  it('збереглося — вихід без діалогу, і збереження пішло раніше за вихід', async () => {
    server();
    let dirty = true;
    const flush = vi.fn(async () => {
      expect(isSessionClosed()).toBe(false);
      dirty = false;

      return true;
    });
    offs.push(registerUnsavedSource('test-ok', { hasUnsaved: () => dirty, flush }));
    show();

    await clickSignOut();

    await waitFor(() => expect(loggedOut()).toBe(true));
    expect(flush).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('unsaved-stay')).toBeNull();
  });
});
