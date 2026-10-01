import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RegistryImpactPage } from '../RegistryImpactPage';
import { testTheme } from '@/test/render';

/**
 * D4: заголовок сторінки впливу не вкладає `<p>` у `<p>` (PageHeader сам обгортає `meta` у Text):
 * React кидає в консоль «<p> cannot be a descendant of <p>».
 * ⛔ Мутація: повернути `meta={<Text …>{code}</Text>}` — тест червоніє.
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('RegistryImpactPage: розмітка', () => {
  it('не друкує помилок React про вкладені абзаци', async () => {
    const errors = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const path = String(input).split('?')[0] ?? '';
        if (path.endsWith('/impact')) return json({ items: [], total: 0, truncated: false });
        return json(null);
      }),
    );

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MemoryRouter initialEntries={['/admin/registries/COMPONENT/impact']}>
            <Routes>
              <Route path="/admin/registries/:code/impact" element={<RegistryImpactPage />} />
            </Routes>
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    expect(await screen.findByText('COMPONENT')).toBeTruthy();
    expect(document.querySelector('p p')).toBeNull();
    expect(errors).not.toHaveBeenCalled();
  });
});
