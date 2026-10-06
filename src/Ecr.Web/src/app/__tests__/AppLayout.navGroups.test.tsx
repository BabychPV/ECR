import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { AppLayout } from '@/app/AppLayout';
import { mantineProviderProps } from '@/shared/theme/provider';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * UI-12: бічне меню групами Work · Configure · Access · Operate (макет
 * `docs/design/hybrid`, `.rail-g`).
 *
 * Що доводиться: (1) розгорнуте меню показує підписи груп, і кожна група —
 * `role="group"` зі своїм ім'ям; (2) група без жодного дозволеного пункту не
 * малюється зовсім; (3) пункти йдуть у порядку макета; (4) згорнуте меню
 * показує роздільники між групами (не над першою) і не додає зупинок Tab;
 * (5) axe без порушень у світлій і темній темі, в обох станах меню.
 */
vi.mock('@/pages/DocumentsPage', () => ({ DocumentsPage: () => null }));

const MeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  // ⚠ Жодного права групи Access (Security.ManageRoles, Period.Configure):
  // група мусить зникнути разом із підписом, а не лишитися порожньою.
  permissions: ['Template.View', 'Registry.View', 'System.ViewHealth'],
  isSimulation: false,
  mustChangePassword: false,
};

const Strings = {
  'nav.collapse': 'Collapse menu',
  'nav.expand': 'Expand menu',
  'nav.documents': 'Documents',
  'nav.myGroups': 'My groups',
  'nav.templates': 'Templates',
  'nav.registries': 'Registries',
  'nav.jobs': 'Jobs',
  'nav.consistency': 'Consistency issues',
  'nav.health': 'Health',
  'nav.group.work': 'Work',
  'nav.group.configure': 'Configure',
  'nav.group.access': 'Access',
  'nav.group.operate': 'Operate',
  'nav.menu': 'Menu',
  'nav.skipToContent': 'Skip to main content',
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function renderShell(scheme: 'light' | 'dark' = 'light'): ReturnType<typeof render> {
  const router = createMemoryRouter(
    [{ path: '/', element: <AppLayout />, children: [{ index: true, element: <div>Page</div> }] }],
    { initialEntries: ['/'] },
  );
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
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

/** Імена пунктів кожної групи — у порядку на екрані. */
function groupsOnScreen(): { name: string; items: string[] }[] {
  return within(navbar())
    .getAllByRole('group')
    .map((group) => ({
      name: group.getAttribute('aria-label') ?? document.getElementById(group.getAttribute('aria-labelledby') ?? '')?.textContent ?? '',
      items: within(group)
        .getAllByRole('link')
        .map((link) => link.getAttribute('aria-label') ?? link.textContent ?? ''),
    }));
}

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('min-width'),
    media: query,
    onchange: null,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    addListener: () => undefined,
    removeListener: () => undefined,
    dispatchEvent: () => false,
  }));
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/me/preferences')) return jsonResponse([]);
      if (url.includes('/api/v1/me')) return jsonResponse(MeResponse);
      if (url.includes('/ui-strings/')) return jsonResponse({ languageCode: 'en', revision: 1, strings: Strings });
      return jsonResponse(null);
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('AppLayout: групи бічного меню (UI-12)', () => {
  it('розгорнуте меню: підписи груп, порядок макета, порожньої групи Access немає', async () => {
    renderShell();
    await screen.findByRole('link', { name: 'Registries' });

    expect(groupsOnScreen()).toEqual([
      { name: 'Work', items: ['Documents', 'My groups'] },
      { name: 'Configure', items: ['Templates', 'Registries'] },
      { name: 'Operate', items: ['Jobs', 'Consistency issues', 'Health'] },
    ]);

    // Підпис — видимий текст (не лише ім'я для читалки), і групи Access немає ніде.
    expect(within(navbar()).getByText('Configure')).toBeTruthy();
    expect(within(navbar()).queryByText('Access')).toBeNull();
    // Розгорнуте меню — без роздільників.
    expect(navbar().querySelectorAll('[data-nav-group-divider]')).toHaveLength(0);
  });

  it('згорнуте меню: роздільник між групами (не над першою), підписів не видно, імена груп лишаються', async () => {
    localStorage.setItem('ecr.navbarCollapsed', 'true');
    renderShell();
    await screen.findByRole('link', { name: 'Registries' });

    expect(navbar().querySelectorAll('[data-nav-group-divider]')).toHaveLength(2);
    expect(navbar().querySelector('[data-nav-group="work"] [data-nav-group-divider]')).toBeNull();
    expect(within(navbar()).queryByText('Configure')).toBeNull();
    expect(groupsOnScreen().map((group) => group.name)).toEqual(['Work', 'Configure', 'Operate']);

    // ⛔ Роздільник — оздоба: не фокусується і схований від читалки.
    for (const divider of navbar().querySelectorAll('[data-nav-group-divider]')) {
      expect(divider.getAttribute('aria-hidden')).toBe('true');
      expect(divider.hasAttribute('tabindex')).toBe(false);
    }
  });
});

describe('AppLayout: групи меню — axe без блокуючих порушень', { timeout: 30_000 }, () => {
  it.each([
    ['light', false],
    ['dark', false],
    ['light', true],
    ['dark', true],
  ] as const)('тема %s, згорнуте: %s', async (scheme, collapsed) => {
    if (collapsed) localStorage.setItem('ecr.navbarCollapsed', 'true');
    const { container } = renderShell(scheme);
    await screen.findByRole('link', { name: 'Registries' });

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
