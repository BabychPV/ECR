import type { JSX, ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SheetActions } from '../SheetActions';

/**
 * B46(2), живий прохід W15s: тост F9 / «Recalculate» показував сирий ідентифікатор задачі
 * («Recalculation queued as job Recalculation-doc96-p202610-s218-…») — `humanizeJobId` такого
 * вигляду не впізнає й віддає його як є. Тепер тост лише каже, що перерахунок аркуша в черзі;
 * шлях до задачі — посилання на `/admin/jobs?id=…`, і лише з `System.ViewHealth` (маршрут
 * `/admin/jobs` закритий цим правом — без нього посилання вело б у 403).
 */
const RawJobId = 'Recalculation-doc96-p202610-s218-0f3c9a7d2b6e4c1a8d5f7e9b3a1c2d4e';

const QueuedText = /Sheet recalculation queued|workflow\.sheetRecalcQueued/i;
const OpenJobLink = /open in the job queue|registries\.impact\.openJob/i;

function mockFetch(permissions: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({
          denies: [],
          grants: {},
          isSimulation: false,
          language: 'en',
          mustChangePassword: false,
          permissions,
          simulatedForUserId: null,
          userId: 9,
          userName: 'tester',
        });
      }
      if (url.includes('/recalculate')) return json({ jobId: RawJobId }, 202);
      if (url.includes('/api/v1/jobs/')) {
        return json({ jobId: RawJobId, state: 'Running', percent: 10, message: null, error: null });
      }

      return json(null);
    }),
  );
}

function JobsProbe(): JSX.Element {
  const location = useLocation();

  return <div data-testid="jobs-route">{`${location.pathname}${location.search}`}</div>;
}

function show(routed: boolean): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const actions = <SheetActions documentId={96} sheetDefId={218} periodKey={202610} state="Draft" />;

  const body: ReactNode = routed ? (
    <MemoryRouter initialEntries={['/documents/96']}>
      <Routes>
        <Route path="/documents/96" element={actions} />
        <Route path="/admin/jobs" element={<JobsProbe />} />
      </Routes>
    </MemoryRouter>
  ) : (
    actions
  );

  render(
    <MantineProvider>
      <Notifications />
      <QueryClientProvider client={client}>{body}</QueryClientProvider>
    </MantineProvider>,
  );
}

async function pressF9(): Promise<void> {
  await screen.findByRole('button', { name: /recalculate/i });
  fireEvent.keyDown(document.body, { key: 'F9', code: 'F9' });
}

afterEach(() => {
  notifications.clean();
  vi.unstubAllGlobals();
});

describe('SheetActions: тост F9 без сирого id задачі (B46(2))', () => {
  it('тост каже «перерахунок аркуша в черзі» і не містить ідентифікатора задачі', async () => {
    mockFetch(['Document.View']);
    show(false);
    await pressF9();

    // Будь-який тост постановки — і старий, і новий текст містять «queued».
    const toast = await screen.findByText(/queued/i);
    // ⛔ RED до фіксу: тост був `workflow.recalcQueued` з `{job}` = сирий `Recalculation-doc96-…`.
    expect(toast.textContent).not.toContain('doc96');
    expect(document.body.textContent).not.toContain(RawJobId);
    expect(toast.textContent).toMatch(QueuedText);
  });

  it('без System.ViewHealth — посилання на задачу немає (не шлях у 403)', async () => {
    mockFetch(['Document.View']);
    show(true);
    await pressF9();

    await screen.findByText(QueuedText);
    expect(screen.queryByRole('link', { name: OpenJobLink })).toBeNull();
  });

  it('з System.ViewHealth — посилання відкриває саме цю задачу на /admin/jobs', async () => {
    mockFetch(['Document.View', 'System.ViewHealth']);
    show(true);
    await pressF9();

    const link = await screen.findByRole('link', { name: OpenJobLink });
    const to = `/admin/jobs?id=${encodeURIComponent(RawJobId)}`;
    expect(link.getAttribute('href')).toBe(to);

    fireEvent.click(link);

    await waitFor(() => expect(screen.getByTestId('jobs-route').textContent).toBe(to));
  });

  it('з System.ViewHealth поза роутером — звичайне посилання з тією самою адресою', async () => {
    mockFetch(['Document.View', 'System.ViewHealth']);
    show(false);
    await pressF9();

    const link = await screen.findByRole('link', { name: OpenJobLink });
    expect(link.getAttribute('href')).toBe(`/admin/jobs?id=${encodeURIComponent(RawJobId)}`);
  });
});
