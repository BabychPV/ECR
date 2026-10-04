import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { ExportButton } from '../ExportButton';
import { testTheme } from '@/test/render';

/**
 * AN-28 P2-2: експорт спершу зберігає набране (L8-01); у цьому вікні кнопка була
 * активною, і другий клік ставив у чергу другу задачу експорту.
 */

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

const posts: string[] = [];
let unregister: (() => void) | null = null;

afterEach(() => {
  unregister?.();
  unregister = null;
  posts.length = 0;
  vi.unstubAllGlobals();
});

describe('AN-28 P2-2: Export під час збереження набраного', () => {
  it('кнопка зайнята, повторний клік не ставить другу задачу', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/export') && !url.includes('/jobs/')) {
          posts.push(url);

          return new Response(JSON.stringify({ jobId: 'job-1' }), {
            status: 202,
            headers: { 'Content-Type': 'application/json' },
          });
        }

        return new Response(JSON.stringify({ jobId: 'job-1', state: 'Running', percent: 10, message: null, error: null }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    let dirty = true;
    unregister = registerUnsavedSource('an28-p2-2-export', {
      hasUnsaved: () => dirty,
      flush: async () => {
        await new Promise((resolve) => setTimeout(resolve, 300));
        dirty = false;

        return true;
      },
    });

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <ExportButton documentId={1} periodKey={202601} language="en" />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const button = screen.getByRole('button', { name: /export/i });
    fireEvent.click(button);
    fireEvent.click(button);

    await waitFor(() => {
      expect(button.getAttribute('aria-busy')).toBe('true');
    });
    fireEvent.click(button);

    await waitFor(() => {
      expect(posts).toHaveLength(1);
    });
    await new Promise((resolve) => setTimeout(resolve, 400));

    expect(posts).toHaveLength(1);
  });
});
