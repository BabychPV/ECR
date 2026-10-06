import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { loadCatalog } from '@/shared/i18n';
import { HealthPage, diagnostics } from '@/pages/admin/HealthPage';

/**
 * `UI-39`: сторінка Health за макетом (`docs/design/hybrid/screens-ops.js`,
 * `/admin/health`) — банер з першою причиною, секції з одним реченням,
 * «Check now», «Copy diagnostics» лише з несекретними фактами.
 */

const shown = vi.hoisted(() => ({ done: vi.fn(), error: vi.fn() }));

vi.mock('@/shared/ui/notify', () => ({
  showDone: shown.done,
  showApiError: shown.error,
}));

const SeededStrings: Record<string, string> = {
  'health.title': 'Health',
  'health.check.db': 'Database',
  'health.check.jobs': 'Background jobs',
  'health.check.sources': 'Collection sources',
  'health.banner.title': '{check} needs attention',
  'health.checkNow': 'Check now',
  'health.copyDiagnostics': 'Copy diagnostics',
  'health.checkedNow': 'Checked just now.',
  'health.diagnosticsCopied': 'Diagnostics copied.',
};

type Check = { name: string; status: string; description: string | null; durationMs: number; data: Record<string, unknown> };

function check(name: string, status: string, description: string): Check {
  return { name, status, description, durationMs: 2, data: {} };
}

function reportOf(status: string, checks: Check[]): unknown {
  return { status, totalDurationMs: 3, checks };
}

/** Секрет, якого «Copy diagnostics» не сміє покласти в буфер. */
const Thumbprint = 'AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12';

const DbReport = reportOf('Healthy', [
  {
    name: 'db',
    status: 'Healthy',
    // `/health/db` — повний опис (там бувають RCSI-скрипт, відбитки); у буфер не йде.
    description: `Key certificate ${Thumbprint} is readable.`,
    durationMs: 3,
    data: { edition: 'Enterprise', partitionsAhead: 15, unreadableKeyCertificates: [Thumbprint] },
  },
]);

const Facts = {
  productVersion: '1.4.2',
  startedAt: '2026-09-19T03:00:00Z',
  environment: 'Production',
  notificationTransport: { isConfigured: true, kind: 'Smtp' },
  logDirectory: 'C:\\ProgramData\\ECR\\logs',
};

let ready: unknown;

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(async () => {
  shown.done.mockReset();
  shown.error.mockReset();
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);

      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      if (url.includes('/health/ready')) return json(ready);
      if (url.includes('/health/db')) return json(DbReport);
      if (url.includes('/health/facts')) return json(Facts);

      return json(null);
    }),
  );

  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <HealthPage />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('UI-39: банер з першою причиною', () => {
  it('Degraded дає банер-попередження з назвою перевірки та її реченням', async () => {
    ready = reportOf('Degraded', [
      check('db', 'Healthy', 'Database is available.'),
      check('sources', 'Degraded', 'Sources with a coverage gap: 1.'),
    ]);
    show();

    const banner = await screen.findByTestId('health-banner');
    expect(banner.getAttribute('data-tone')).toBe('warning');
    expect(banner.textContent).toContain('Collection sources needs attention');
    expect(banner.textContent).toContain('Sources with a coverage gap: 1.');
  });

  it('Unhealthy випереджає Degraded, навіть коли стоїть нижче за порядком секцій', async () => {
    ready = reportOf('Unhealthy', [
      check('db', 'Degraded', 'Partitions ahead: 2.'),
      check('jobs', 'Unhealthy', 'The scheduler is unavailable.'),
    ]);
    show();

    const banner = await screen.findByTestId('health-banner');
    expect(banner.getAttribute('data-tone')).toBe('danger');
    expect(banner.textContent).toContain('Background jobs needs attention');
    expect(banner.textContent).not.toContain('Partitions ahead');
  });

  it('усе Healthy — банера немає, секції з реченнями є', async () => {
    ready = reportOf('Healthy', [
      check('db', 'Healthy', 'Database is available.'),
      check('jobs', 'Healthy', 'The scheduler is running.'),
    ]);
    show();

    expect(await screen.findByText('The scheduler is running.')).toBeDefined();
    expect(screen.queryByTestId('health-banner')).toBeNull();
    expect(document.querySelector('[data-health-section="jobs"]')).not.toBeNull();
    expect(screen.getByRole('heading', { name: 'Background jobs' })).toBeDefined();
  });
});

describe('UI-39: «Check now» і «Copy diagnostics»', () => {
  it('«Check now» запитує стан наново й каже, що перевірено', async () => {
    ready = reportOf('Healthy', [check('db', 'Healthy', 'Database is available.')]);
    show();

    await screen.findByText('Database is available.');
    const before = vi.mocked(fetch).mock.calls.filter(([url]) => String(url).includes('/health/ready')).length;

    fireEvent.click(await screen.findByRole('button', { name: 'Check now' }));

    await waitFor(() => expect(shown.done).toHaveBeenCalledWith('Checked just now.'));
    const after = vi.mocked(fetch).mock.calls.filter(([url]) => String(url).includes('/health/ready')).length;
    expect(after).toBeGreaterThan(before);
  });

  it('«Copy diagnostics» кладе в буфер звіт без секретів', async () => {
    const writeText = vi.fn(async () => Promise.resolve());
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
    ready = reportOf('Degraded', [check('sources', 'Degraded', 'Sources with a coverage gap: 1.')]);
    show();

    await screen.findByText('1.4.2');
    fireEvent.click(await screen.findByRole('button', { name: 'Copy diagnostics' }));

    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(1));
    const text = String((writeText.mock.calls[0] as unknown[])[0]);
    expect(text).toContain('Overall: Degraded');
    expect(text).toContain('sources: Degraded · Sources with a coverage gap: 1.');
    expect(text).toContain('db.edition: Enterprise');
    expect(text).toContain('Version: 1.4.2');
    expect(text).not.toContain(Thumbprint);
    expect(shown.done).toHaveBeenCalledWith('Diagnostics copied.');
  });
});

describe('UI-39: diagnostics() — білий список', () => {
  it('з /health/db бере лише відомі поля, без опису й службових ключів', () => {
    const text = diagnostics({
      ready: { ready: false, report: reportOf('Unhealthy', [check('db', 'Unhealthy', 'Database is unavailable.')]) as never },
      db: DbReport as never,
      facts: Facts,
      checkedAt: '2026-10-06T20:00:00.000Z',
    });

    expect(text.split('\n')[0]).toBe('ECR diagnostics · 2026-10-06T20:00:00.000Z');
    expect(text).toContain('Overall: Unhealthy (not ready)');
    expect(text).toContain('db: Unhealthy · Database is unavailable.');
    expect(text).toContain('db.partitionsAhead: 15');
    expect(text).toContain('Notifications: Smtp');
    expect(text).not.toContain('unreadableKeyCertificates');
    expect(text).not.toContain(Thumbprint);
  });
});
