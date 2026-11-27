import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { MappingPreviewPage } from '@/pages/admin/MappingPreviewPage';
import { PipelinePage } from '@/pages/admin/PipelinePage';
import { PiDataPage } from '@/pages/admin/PiDataPage';
import { testTheme } from '@/test/render';

/**
 * ✎ DOCUX (огляд UI 27.11): без джерел Mapping preview / Data pipeline / PI data показували «No collection sources
 * configured» І другу підказку «Pick a source entity above…» з порожнім вибором. Тепер — ОДИН порожній стан з переходом до
 * Sources; вибору сутності й другої підказки немає.
 */
const json = (body: unknown): Response => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

function show(page: JSX.Element, path: string): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: ['Integration.Manage'], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/api/v1/sources')) return json([]);
      return json(null);
    }),
  );
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter initialEntries={[path]}>{page}</MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('без джерел — один порожній стан із переходом до Sources', () => {
  it.each([
    ['Mapping preview', <MappingPreviewPage key="m" />, '/admin/mapping', 'mapping.pickHint'],
    ['Data pipeline', <PipelinePage key="p" />, '/admin/pipeline', 'pipeline.pickHint'],
    ['PI data', <PiDataPage key="d" />, '/admin/pi-data', 'piData.pickHint'],
  ])('%s', async (_name, page, path, pickHint) => {
    show(page, path);

    const link = await screen.findByRole('link', { name: '⟦sources.goToSources⟧' });
    expect(link.getAttribute('href')).toBe('/admin/sources');
    expect(screen.getByText('⟦sources.empty⟧')).toBeTruthy();
    expect(screen.queryByText(`⟦${pickHint}⟧`)).toBeNull();
    expect(screen.queryByRole('textbox')).toBeNull();
  });
});
