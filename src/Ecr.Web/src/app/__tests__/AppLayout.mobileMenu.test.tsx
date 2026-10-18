import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { theme } from '@/shared/theme/theme';
import { AppLayout } from '@/app/AppLayout';

/**
 * CL-16 (B-5): мобільне меню. Бургер каже стан (`aria-expanded`) і ціль (`aria-controls`),
 * Esc закриває меню й повертає фокус на бургер (раніше — лише повторний клік).
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function stubViewport(width: number): void {
  vi.stubGlobal('matchMedia', (query: string) => {
    const min = /min-width:\s*(\d+(?:\.\d+)?)px/.exec(query);

    return {
      matches: min === null ? false : width >= Number(min[1]),
      media: query,
      onchange: null,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      addListener: () => undefined,
      removeListener: () => undefined,
      dispatchEvent: () => false,
    };
  });
}

function renderShell(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
      // Лише `/api/v1/me` — об'єкт; решта (завдання, налаштування) — порожні масиви.
      const body = /\/api\/v1\/me(\?|$)/.test(url)
        ? {
            userId: 1,
            userName: 'tester',
            language: 'en',
            permissions: ['Registry.View'],
            isSimulation: false,
            mustChangePassword: false,
          }
        : [];

      return Promise.resolve(
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
  const router = createMemoryRouter(
    [{ path: '/', element: <AppLayout />, children: [{ index: true, element: <div>Page</div> }] }],
    { initialEntries: ['/'] },
  );

  render(
    <MantineProvider theme={theme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('AppLayout: мобільне меню (CL-16)', () => {
  it('бургер: aria-expanded відповідає стану, aria-controls вказує на наявну панель', async () => {
    stubViewport(500);
    renderShell();

    const burger = await screen.findByRole('button', { name: /nav\.menu/ });
    expect(burger.getAttribute('aria-expanded')).toBe('false');

    const controlled = burger.getAttribute('aria-controls');
    expect(controlled).toBeTruthy();
    expect(document.getElementById(controlled ?? '')).not.toBeNull();

    fireEvent.click(burger);
    expect(burger.getAttribute('aria-expanded')).toBe('true');

    fireEvent.click(burger);
    expect(burger.getAttribute('aria-expanded')).toBe('false');
  });

  it('Esc закриває відкрите меню і повертає фокус на бургер', async () => {
    stubViewport(500);
    renderShell();

    const burger = await screen.findByRole('button', { name: /nav\.menu/ });
    fireEvent.click(burger);
    expect(burger.getAttribute('aria-expanded')).toBe('true');

    // Фокус — усередині панелі, а не на бургері: інакше «повернення» нічого б не доводило.
    const inside = document.getElementById(burger.getAttribute('aria-controls') ?? '')?.querySelector('a, button');
    (inside as HTMLElement | null)?.focus();
    expect(document.activeElement).not.toBe(burger);

    fireEvent.keyDown(document.activeElement ?? document.body, { key: 'Escape' });

    expect(burger.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).toBe(burger);
  });

  it('Esc, який уже обробили (defaultPrevented), меню не закриває', async () => {
    stubViewport(500);
    renderShell();

    const burger = await screen.findByRole('button', { name: /nav\.menu/ });
    fireEvent.click(burger);

    const event = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
    event.preventDefault();
    document.body.dispatchEvent(event);

    expect(burger.getAttribute('aria-expanded')).toBe('true');
  });

  it('Esc при закритому меню нічого не робить (фокус не краде)', async () => {
    stubViewport(500);
    renderShell();

    const burger = await screen.findByRole('button', { name: /nav\.menu/ });
    fireEvent.keyDown(document.body, { key: 'Escape' });

    expect(burger.getAttribute('aria-expanded')).toBe('false');
    expect(document.activeElement).not.toBe(burger);
  });
});
