import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SourcesPage } from '@/pages/admin/SourcesPage';

/**
 * P3 живого проходу екрана джерел: тост «Collection queued» не вів до задачі —
 * людина бачила номер, але не мала шляху подивитись, чим збір скінчився.
 * Тепер у тості посилання на `/admin/jobs?id=…` (лише з `System.ViewHealth`:
 * без права екран черги — 403).
 */
const sources = [
  {
    id: 1,
    code: 'FLD-1',
    displayName: 'Field weather feed',
    entityPath: null,
    isActive: true,
    lastRun: null,
    oldestGap: null,
    transport: 'Rest',
  },
];

const JobId = 'collect:7#2026';

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function mockFetch(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/collect')) return Promise.resolve(json({ jobId: JobId }, 202));
      if (url.includes('/api/v1/me')) {
        return Promise.resolve(
          json({
            denies: [],
            grants: {},
            isSimulation: false,
            language: 'en',
            mustChangePassword: false,
            permissions,
            simulatedForUserId: null,
            userId: 1,
            userName: 'tester',
          }),
        );
      }
      if (url.includes('/api/v1/data-sources')) return Promise.resolve(json([]));
      if (url.includes('/api/v1/sources')) return Promise.resolve(json(sources));

      return Promise.resolve(json(null));
    }),
  );
}

function JobsProbe(): JSX.Element {
  const location = useLocation();

  return <div data-testid="jobs-route">{`${location.pathname}${location.search}`}</div>;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <Notifications />
      <MemoryRouter initialEntries={['/admin/sources']}>
        <QueryClientProvider client={client}>
          <Routes>
            <Route path="/admin/sources" element={<SourcesPage />} />
            <Route path="/admin/jobs" element={<JobsProbe />} />
          </Routes>
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

async function collect(): Promise<void> {
  await screen.findByText('Field weather feed');
  const row = screen.getByText('Field weather feed').closest('tr');
  fireEvent.click(row?.querySelector('button') as HTMLButtonElement);
}

afterEach(() => {
  notifications.clean();
  vi.unstubAllGlobals();
});

describe('SourcesPage: тост «Collection queued» веде до задачі', () => {
  it('з System.ViewHealth — посилання на /admin/jobs?id=… відкриває задачу', async () => {
    mockFetch(['Integration.Manage', 'System.ViewHealth']);
    show();
    await collect();

    const link = await screen.findByRole('link', { name: /open in the job queue|registries\.impact\.openJob/i });
    expect(link.getAttribute('href')).toBe(`/admin/jobs?id=${encodeURIComponent(JobId)}`);

    fireEvent.click(link);

    await waitFor(() =>
      expect(screen.getByTestId('jobs-route').textContent).toBe(`/admin/jobs?id=${encodeURIComponent(JobId)}`),
    );
  });

  it('без System.ViewHealth — тост без посилання (не шлях у 403)', async () => {
    mockFetch(['Integration.Manage']);
    show();
    await collect();

    await screen.findByText(/queued|sources\.queued/i);
    expect(screen.queryByRole('link', { name: /open in the job queue|registries\.impact\.openJob/i })).toBeNull();
  });
});
