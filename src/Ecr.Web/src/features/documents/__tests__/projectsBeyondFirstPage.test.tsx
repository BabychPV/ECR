import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { testTheme } from '@/test/render';

/**
 * AN-39 / L8-10 (= L9-15): `DocumentPage` і `CreateDocumentModal` читали `['projects']` з
 * `?limit=200` - проєкт поза першою сторінкою не знаходився: блокування архіву
 * не спрацьовувало, а в діалозі створення його не було в переліку. Спільний ключ кешу
 * `['projects']` ще й підміняв дані сусідам (X-07). Тепер обидва йдуть `fetchAllProjects`.
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const route = url.split('?')[0] ?? '';

      if (route.endsWith('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 't' });
      }

      if (route.endsWith('/api/v1/languages')) return json([{ code: 'en', nameNative: 'English', isDefault: true }]);

      if (route.endsWith('/api/v1/projects')) {
        return url.includes('cursor=c2')
          ? json({ items: [{ id: 201, code: 'LATE', status: 'Active' }], nextCursor: null, totalCount: 2 })
          : json({ items: [{ id: 1, code: 'FIRST', status: 'Active' }], nextCursor: 'c2', totalCount: 2 });
      }

      return json({ items: [], nextCursor: null, totalCount: 0 });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

function* sources(dir: string): Generator<string> {
  for (const name of readdirSync(dir)) {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) {
      if (name !== '__tests__') yield* sources(full);
    } else if (/\.tsx?$/.test(name)) yield full;
  }
}

describe('L8-10: проєкти поза першою сторінкою', () => {
  it('CreateDocumentModal: проєкт з другої сторінки є в переліку', async () => {
    mockServer();
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <CreateDocumentModal opened onClose={() => {}} />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    const select = await waitFor(() => screen.getByLabelText(/documents\.project/));
    fireEvent.click(select);

    expect(await screen.findByRole('option', { name: /LATE/ })).toBeDefined();
  });

  it('сторож: жоден файл клієнта не просить проєкти через ?limit=200', () => {
    const root = path.resolve(__dirname, '../../..');
    const needle = ['/api/v1/projects', '?limit=200'].join('');
    const offenders: string[] = [];

    for (const file of sources(root)) {
      if (readFileSync(file, 'utf8').includes(needle)) offenders.push(path.relative(root, file));
    }

    expect(offenders).toEqual([]);
  });
});
