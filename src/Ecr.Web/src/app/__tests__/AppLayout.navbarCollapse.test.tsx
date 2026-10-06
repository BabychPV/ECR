import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';
import { setNavbarCollapsed } from '@/shared/theme/navbarCollapse';

// Фокус на пункті меню прогріває чанк сторінки (`useRoutePrefetch`); справжній
// імпорт доїхав би вже після кінця файла.
vi.mock('@/pages/DocumentsPage', () => ({ DocumentsPage: () => null }));
vi.mock('@/pages/admin/RegistriesPage', () => ({ RegistriesPage: () => null }));

/**
 * Бічне меню згортається до іконок і пам'ятає це (запит людини 06.10).
 *
 * Що доводиться: (1) кнопка міняє стан і своє ім'я, `aria-expanded` чесний;
 * (2) у згорнутому меню пункти без видимого тексту, але з доступним ім'ям і
 * підказкою на фокусі; (3) вибір пишеться в `localStorage` (перший кадр після
 * F5 уже вузький) і на сервер (`navbarCollapsed`); (4) значення сервера
 * застосовується при вході; (5) на мобільному «лише іконки» не діє.
 */
const MeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  permissions: ['Template.Edit', 'Registry.View'],
  isSimulation: false,
  mustChangePassword: false,
};

const Strings = {
  'nav.collapse': 'Collapse menu',
  'nav.expand': 'Expand menu',
  'nav.documents': 'Documents',
  'nav.registries': 'Registries',
  'nav.myGroups': 'My groups',
  'nav.menu': 'Menu',
  'nav.skipToContent': 'Skip to main content',
};

const StorageKey = 'ecr.navbarCollapsed';

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

let serverPreferences: unknown[] = [];
let puts: { url: string; body: unknown }[] = [];

function stubViewport(desktop: boolean): void {
  vi.stubGlobal('matchMedia', (query: string) => ({
    // Лише запит ширини меню відповідає `desktop`; тема (prefers-color-scheme) — ні.
    matches: query.includes('min-width') ? desktop : false,
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  }));
}

function renderAppLayout(): ReturnType<typeof render> {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const router = createMemoryRouter(
    [
      {
        path: '/',
        element: <AppLayout />,
        children: [{ index: true, element: <div>Page</div> }],
      },
    ],
    { initialEntries: ['/'] },
  );

  return render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={client}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

function navbar(): HTMLElement {
  const nav = document.querySelector('nav');
  if (nav === null) throw new Error('navbar not rendered');
  return nav;
}

/** Видимий текст пункту меню (прихований опис підказки `Hint` — не в рахунок). */
function visibleLabel(text: string): HTMLElement | null {
  return within(navbar()).queryAllByText(text).find((node) => node.closest('[hidden]') === null) ?? null;
}

/** Каркас домалювався: сесія приїхала, меню в дереві. */
async function shellReady(): Promise<void> {
  await screen.findByRole('link', { name: 'Registries' });
  // Лінивий `Hint` каркаса доїхав і замінив fallback — так, як у браузері
  // одразу після першого кадру, ДО того, як людина щось натисне.
  await act(async () => {
    await import('@/shared/ui/Hint');
  });
}

describe('AppLayout: бічне меню згортається до іконок', () => {
  beforeEach(() => {
    localStorage.clear();
    serverPreferences = [];
    puts = [];
    stubViewport(true);
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);

        if (url.includes('/api/v1/me/preferences')) {
          if (init?.method === 'PUT') {
            puts.push({ url, body: JSON.parse(String(init.body)) });
            return jsonResponse({ key: 'navbarCollapsed', value: true, updatedAt: '2026-10-06T00:00:00Z' });
          }
          return jsonResponse(serverPreferences);
        }
        if (url.includes('/api/v1/me')) return jsonResponse(MeResponse);
        if (url.includes('/ui-strings/')) {
          return jsonResponse({ languageCode: 'en', revision: 1, strings: Strings });
        }

        return jsonResponse(null);
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    localStorage.clear();
  });

  it('кнопка згортає меню: пункти без тексту, з іменем, стан у localStorage і на сервері', async () => {
    renderAppLayout();
    const user = userEvent.setup();

    const collapse = await screen.findByRole('button', { name: 'Collapse menu' });
    expect(collapse.getAttribute('aria-expanded')).toBe('true');
    expect(visibleLabel('Documents')).not.toBeNull();

    await user.click(collapse);

    const expand = await screen.findByRole('button', { name: 'Expand menu' });
    expect(expand.getAttribute('aria-expanded')).toBe('false');
    expect(expand.getAttribute('aria-controls')).toBe('app-navbar-items');
    expect(document.getElementById('app-navbar-items')).not.toBeNull();

    // Видимого тексту немає, доступне ім'я — є; активний пункт лишається активним.
    const documents = within(navbar()).getByRole('link', { name: 'Documents' });
    expect(visibleLabel('Documents')).toBeNull();
    expect(documents.hasAttribute('data-active')).toBe(true);

    expect(localStorage.getItem(StorageKey)).toBe('true');
    await waitFor(() => {
      expect(puts).toEqual([{ url: expect.stringContaining('/me/preferences/navbarCollapsed'), body: true }]);
    });

    // І назад.
    await user.click(expand);
    await waitFor(() => {
      expect(visibleLabel('Documents')).not.toBeNull();
    });
    expect(localStorage.getItem(StorageKey)).toBe('false');
  });

  it.each(['{Enter}', ' '])(
    'клавіша %j на кнопці перемикає меню, а фокус лишається на кнопці в обидва боки (WCAG 2.4.3)',
    async (key) => {
      renderAppLayout();
      await shellReady();
      const user = userEvent.setup();

      const collapse = screen.getByRole('button', { name: 'Collapse menu' });
      collapse.focus();
      expect(document.activeElement).toBe(collapse);

      await user.keyboard(key);
      const expand = await screen.findByRole('button', { name: 'Expand menu' });
      // ⛔ Той самий вузол DOM, не лише «якась кнопка з фокусом»: перемонтована
      // кнопка теж могла б отримати фокус, але після автофокуса, не від людини.
      expect(expand).toBe(collapse);
      expect(document.activeElement).toBe(expand);

      await user.keyboard(key);
      expect(await screen.findByRole('button', { name: 'Collapse menu' })).toBe(collapse);
      expect(document.activeElement).toBe(collapse);
    },
  );

  it('пункт меню під фокусом лишається тим самим вузлом після згортання', async () => {
    renderAppLayout();
    await shellReady();

    const registries = within(navbar()).getByRole('link', { name: 'Registries' });
    registries.focus();
    act(() => {
      setNavbarCollapsed(true);
    });

    expect(await screen.findByRole('button', { name: 'Expand menu' })).toBeTruthy();
    expect(within(navbar()).getByRole('link', { name: 'Registries' })).toBe(registries);
    expect(document.activeElement).toBe(registries);
  });

  it('у згорнутому меню ім\'я пункту не дублюється описом (читач не каже назву двічі)', async () => {
    localStorage.setItem(StorageKey, 'true');
    renderAppLayout();
    await shellReady();

    const documents = within(navbar()).getByRole('link', { name: 'Documents' });
    documents.focus();
    await screen.findByRole('tooltip');
    expect(documents.getAttribute('aria-describedby')).toBeNull();
  });

  it('у згорнутому меню назва пункту видна підказкою на фокусі з клавіатури', async () => {
    localStorage.setItem(StorageKey, 'true');
    renderAppLayout();
    await shellReady();

    const documents = within(navbar()).getByRole('link', { name: 'Documents' });
    documents.focus();

    const tip = await screen.findByRole('tooltip');
    expect(tip.textContent).toBe('Documents');
  });

  it('після F5 меню вузьке з першого кадру — стан читається з localStorage синхронно', async () => {
    localStorage.setItem(StorageKey, 'true');
    renderAppLayout();

    // Перший же рендер каркаса — уже кнопка «розгорнути», без проміжного широкого.
    await shellReady();
    expect(screen.getByRole('button', { name: 'Expand menu' })).toBeTruthy();
    expect(visibleLabel('Documents')).toBeNull();
    // Локальний вибір без серверного переноситься на сервер (міграція `BE-20`).
    await waitFor(() => {
      expect(puts.map((put) => put.body)).toEqual([true]);
    });
  });

  it('значення сервера застосовується при вході', async () => {
    serverPreferences = [{ key: 'navbarCollapsed', value: true, updatedAt: '2026-10-06T00:00:00Z' }];
    renderAppLayout();

    expect(await screen.findByRole('button', { name: 'Expand menu' })).toBeTruthy();
    expect(localStorage.getItem(StorageKey)).toBe('true');
    // Відлуння значення сервера назад на сервер не йде.
    expect(puts).toEqual([]);
  });

  it('на мобільному «лише іконки» не діє: шухляда показує назви', async () => {
    stubViewport(false);
    localStorage.setItem(StorageKey, 'true');
    renderAppLayout();
    await shellReady();

    expect(visibleLabel('Documents')).not.toBeNull();
  });
});

describe('тексти кнопки згортання в сіді', () => {
  const seed = readFileSync(
    path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
    'utf8',
  );

  it.each(['nav.collapse', 'nav.expand'])('%s є для en, ru і kz', (key) => {
    for (const lang of ['en', 'ru', 'kz']) {
      expect(seed).toMatch(new RegExp(`\\(N'${key.replace('.', '\\.')}', N'${lang}', N'[^']+'`));
    }
  });
});
