import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ExpressionsPage } from '@/pages/admin/ExpressionsPage';
import { testTheme } from '@/test/render';

/**
 * D-2 (P2): редактор брав версії лише першого шаблону зі списку. Перший без
 * версій → для інших шаблонів перевірка й підказки були недоступні.
 * Заглушка редактора — як у сусідньому `sourceError`-наборі (Monaco у jsdom дорогий).
 */
vi.mock('@/features/expressions/ExpressionEditor', () => ({
  ExpressionEditor: (props: { placement: { templateVersionId?: number } }): JSX.Element => (
    <div data-testid="expression-editor" data-tv={String(props.placement.templateVersionId)} />
  ),
}));

const requested: string[] = [];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

function version(id: number): unknown {
  return {
    id,
    version: '1.0.0.0',
    status: 'Published',
    presentationRevision: 0,
    clonedFromVersionId: null,
    publishedAt: '2026-09-12T00:00:00Z',
  };
}

function mockServer(): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';
      requested.push(path);

      if (path.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: {} });
      }
      if (/\/api\/v1\/templates\/1\/versions$/.test(path)) {
        return json({ items: [], nextCursor: null, totalCount: 0 });
      }
      if (/\/api\/v1\/templates\/2\/versions$/.test(path)) {
        return json({ items: [version(20)], nextCursor: null, totalCount: 1 });
      }
      if (path.endsWith('/api/v1/templates')) {
        return json({
          items: [
            { id: 1, code: 'EMPTY_FIRST', versionCount: 0 },
            { id: 2, code: 'HAS_VERSIONS', versionCount: 1 },
          ],
          nextCursor: null,
          totalCount: 2,
        });
      }

      return json(null);
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  requested.length = 0;
});

describe('ExpressionsPage: явний вибір шаблону (D-2)', () => {
  it('перший шаблон без версій — за умовчанням береться перший, що має версії', async () => {
    mockServer();
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <ExpressionsPage />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await waitFor(() => {
      expect(requested.some((p) => p.endsWith('/templates/2/versions'))).toBe(true);
    });
    await waitFor(() => {
      const select = screen.getByRole('textbox', {
        name: /expressions\.templateVersion/,
      });
      expect((select as HTMLInputElement).disabled).toBe(false);
    });

    expect(requested.some((p) => p.endsWith('/templates/1/versions'))).toBe(false);

    // Версія другого шаблону доступна й потрапляє в розміщення редактора.
    fireEvent.click(screen.getByRole('textbox', { name: /expressions\.templateVersion/ }));
    fireEvent.click(await screen.findByRole('option', { name: /1\.0\.0\.0/ }));

    await waitFor(() => {
      expect(screen.getByTestId('expression-editor').getAttribute('data-tv')).toBe('20');
    });
  });

  it('шаблон можна змінити явно — версії беруться вже його', async () => {
    mockServer();
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider
          client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
        >
          <ExpressionsPage />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await waitFor(() => {
      const select = screen.getByRole('textbox', { name: /templates\.card/ });
      expect((select as HTMLInputElement).disabled).toBe(false);
    });
    fireEvent.click(screen.getByRole('textbox', { name: /templates\.card/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'EMPTY_FIRST' }));

    await waitFor(() => {
      expect(requested.some((p) => p.endsWith('/templates/1/versions'))).toBe(true);
    });
  });
});
