import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { AppLayout } from '@/app/AppLayout';
import { mantineProviderProps } from '@/shared/theme/provider';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * Каркас зі ЗГОРНУТИМ бічним меню («лише іконки») — axe в обох темах.
 *
 * ⚠ Головний ризик згорнутого меню для доступності — посилання без доступного
 * імені (іконка `aria-hidden`, тексту немає). Правило `link-name` ловить саме
 * його; `button-name` — кнопку згортання без підпису.
 */
vi.mock('@/pages/DocumentsPage', () => ({ DocumentsPage: () => null }));

const MeResponse = {
  userId: 1,
  userName: 'tester',
  language: 'en',
  permissions: ['Template.Edit', 'Registry.View'],
  isSimulation: false,
  mustChangePassword: false,
};

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

beforeEach(() => {
  localStorage.setItem('ecr.navbarCollapsed', 'true');
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
      if (url.includes('/ui-strings/')) {
        return jsonResponse({
          languageCode: 'en',
          revision: 1,
          strings: {
            'nav.expand': 'Expand menu',
            'nav.documents': 'Documents',
            'nav.registries': 'Registries',
            'nav.myGroups': 'My groups',
          },
        });
      }
      return jsonResponse(null);
    }),
  );
});

describe('AppLayout зі згорнутим меню — axe без блокуючих порушень', { timeout: 30_000 }, () => {
  it.each(['light', 'dark'] as const)('тема %s', async (scheme) => {
    const router = createMemoryRouter(
      [{ path: '/', element: <AppLayout />, children: [{ index: true, element: <div>Page</div> }] }],
      { initialEntries: ['/'] },
    );
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const { container } = render(
      <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
        <QueryClientProvider client={client}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByRole('link', { name: 'Registries' });
    expect(screen.getByRole('button', { name: 'Expand menu' })).toBeTruthy();

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
