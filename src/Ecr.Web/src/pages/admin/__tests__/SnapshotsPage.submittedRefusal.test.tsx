import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type {
  PagedProjects,
  ReportDefinition,
  ReportSnapshotSummary,
} from '@/api/types';
import { loadCatalog } from '@/shared/i18n';
import { SnapshotsPage } from '../SnapshotsPage';
import { testTheme } from '@/test/render';

// ⚠ Тост — через `notifications.show`; контейнера `<Notifications />` у тесті немає,
// тож перевіряємо сам виклик (той самий прийом, що й `useStaleResultsReminder.test`).
const toast = vi.hoisted(() => vi.fn());
vi.mock('@mantine/notifications', () => ({ notifications: { show: toast, hide: vi.fn() } }));

/**
 * R7-Y8 / Y8-01: побудова над ПОДАНИМ зрізом (ФВ-9.17).
 *
 * ⛔ Сервер відмовляє задачі кодом `ECR-RPT-0409` («зріз подано»). Загальний
 * текст цього коду в каталозі — «вже опубліковано: потрібна нова версія»; на
 * сторінці зрізів він хибно веде до нової версії опису (сервер відмовить і їй).
 * Тост має назвати причину й шлях: повернути дані в роботу (Reopen).
 *
 * Мутація: прибрати гілку `ECR-RPT-0409` у тості `SnapshotsPage` — тест червоний
 * (показано загальний текст коду).
 *
 * Тести CI, локально не запускались.
 */

const SeededStrings: Record<string, string> = {
  'snapshots.title': 'Report snapshots',
  'snapshots.build': 'Build snapshot',
  'snapshots.buildHint': 'A snapshot is immutable.',
  'snapshots.queued': 'Build queued as job {job}.',
  'snapshots.built': 'Snapshot built.',
  'snapshots.buildFailed': 'Snapshot build failed.',
  'snapshots.code': 'Report code',
  'snapshots.codeHint': 'The report definition to build from.',
  'snapshots.builtAt': 'Built at',
  'snapshots.rows': 'Rows',
  'snapshots.status': 'Status',
  'snapshots.hash': 'Content hash',
  'snapshots.current': 'current',
  'snapshots.empty': 'No snapshots built yet',
  'snapshots.emptyHint': 'SSRS reads snapshots, not live data.',
  'snapshots.pickReport': 'Pick a report',
  'snapshots.noPublished': 'No report definition has a published version yet.',
  'documents.project': 'Project',
  'documents.period': 'Period',
  'periods.pickProject': 'Pick a project',
  'reportDefs.manage': 'Manage report definitions',
  'common.loading': 'Loading',
  'snapshots.buildRefusedSubmitted': 'This period was submitted: reopen the data before building a new snapshot.',
  'err.ECR-RPT-0409': 'Already published: a new version is needed',
};

const project = {
  id: 1,
  code: 'AUDIT_SMOKE_PRJ',
  currentPeriodId: null,
  periodCount: 12,
  periodKind: 'Monthly',
  status: 'Active',
  timeZoneId: 'Etc/UTC',
} as unknown as PagedProjects['items'][number];

const reportDef = {
  id: 1,
  code: 'IEC',
  isActive: true,
  isRegulatory: true,
  // ⚠ Форма з сервера — `{ values: { <мова>: … } }` (`shared/i18n/localized.ts`),
  // а не плаский `{ en: … }`: заглушений `<select>` підпису опції не читав,
  // тож хибна форма фікстури лишалася непоміченою.
  nameL10n: { values: { en: 'Industrial Environmental Control (quarterly)' } },
  versions: [
    {
      id: 1,
      columnsJson: '[]',
      rulesJson: '[]',
      status: 'Published',
      createdAt: '2026-01-01T00:00:00Z',
    },
  ],
} as unknown as ReportDefinition;

const builtSnapshot = {
  id: 100,
  builtAt: '2026-09-14T12:00:00Z',
  contentHash: 'abc123',
  isCurrent: true,
  periodKey: 202609,
  projectId: 1,
  reportVersionId: 1,
  rowCount: 42,
  status: 'Approved',
} as unknown as ReportSnapshotSummary;

/** Симулює фонову задачу, що падає відмовою над поданим зрізом. */
function mockApi(): { snapshotBuilt: boolean } {
  const state = { snapshotBuilt: false };

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';

      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: SeededStrings });
      }

      if (url.endsWith('/api/v1/me')) {
        return json({
          userId: 1,
          userName: 'bootstrap',
          language: 'en',
          permissions: ['Report.BuildSnapshot', 'Report.EditDefinition'],
          isSimulation: false,
          denies: [],
          grants: {},
          mustChangePassword: false,
          simulatedForUserId: null,
        });
      }

      if (url.includes('/api/v1/projects')) {
        return json({ items: [project], nextCursor: null, totalCount: 1 } satisfies PagedProjects);
      }

      if (url.includes('/api/v1/reports/snapshots')) {
        return json(state.snapshotBuilt ? [builtSnapshot] : []);
      }

      if (url.includes('/api/v1/reports') && !url.includes('/build')) {
        return json([reportDef]);
      }

      if (url.includes('/api/v1/reports/IEC/build') && method === 'POST') {
        return json({ jobId: 'job-1' }, 202);
      }

      // ⛔ Задача падає вердиктом «зріз періоду подано» (`ReportSnapshotBuilder.FrozenRefusal`).
      if (url.includes('/api/v1/jobs/job-1')) {
        return json({
          jobId: 'job-1',
          state: 'Failed',
          percent: 10,
          message: null,
          error: 'Snapshot 100 was submitted.',
          errorCode: 'ECR-RPT-0409',
        });
      }

      throw new Error(`Немає мока для ${method} ${url}`);
    }),
  );

  return state;
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SnapshotsPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  toast.mockClear();
});

describe('SnapshotsPage: побудова над поданим зрізом (R7-Y8 / Y8-01)', () => {
  it('відмова ECR-RPT-0409 показує причину й шлях (Reopen), а не загальний текст коду', async () => {
    mockApi();
    await loadCatalog('en', 'private');
    show();

    await screen.findByText('No snapshots built yet');

    fireEvent.click(await screen.findByLabelText('Project'));
    fireEvent.click(await screen.findByRole('option', { name: 'AUDIT_SMOKE_PRJ' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Build snapshot' }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByLabelText('Report code'));
    fireEvent.click(
      await screen.findByRole('option', {
        name: 'Industrial Environmental Control (quarterly) (IEC)',
      }),
    );

    fireEvent.click(within(dialog).getByRole('button', { name: 'Build snapshot' }));

    await waitFor(() => {
      expect(toast).toHaveBeenCalledWith(
        expect.objectContaining({
          color: 'statusError',
          message: 'This period was submitted: reopen the data before building a new snapshot.',
        }),
      );
    });
    expect(toast).not.toHaveBeenCalledWith(
      expect.objectContaining({ message: 'Already published: a new version is needed' }),
    );
  });
});
