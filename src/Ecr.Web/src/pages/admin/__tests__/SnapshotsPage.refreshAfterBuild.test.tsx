import type { JSX } from 'react';
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

/**
 * UI-аудит, lane 6: «Report snapshots list does not refresh after a
 * successful build — page is stuck showing "No snapshots built yet"»
 * (`docs/build/audit-drafts/lane6-snapshot-list-no-refresh-after-build.md`,
 * severity High).
 *
 * ⛔ Побудова повертає `202` з `jobId` (фонова задача), а стара версія
 * інвалідувала `['snapshots']` ОДРАЗУ на цій відповіді — до того, як
 * задача встигала хоч щось записати. Перезапит незмінно приходив
 * порожнім, і сторінка лишалася на «зрізів іще нема» назавжди без
 * жодного опитування.
 *
 * ⚠ `Select` (`@mantine/core`) зависає під jsdom (`Q-299`) — заглушено
 * стандартним стабом (проєкт-пікер у шапці, вибір звіту в модалці).
 */
vi.mock('@mantine/core', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mantine/core')>();

  type StubOption = { value: string; label: string };
  type StubSelectProps = {
    data?: (string | StubOption)[];
    value?: string | null;
    onChange?: (value: string | null) => void;
    label?: string;
    placeholder?: string;
    'aria-label'?: string;
  };

  function StubSelect(props: StubSelectProps): JSX.Element {
    const options = (props.data ?? []).map((item) =>
      typeof item === 'string' ? { value: item, label: item } : item,
    );

    return (
      <select
        aria-label={props['aria-label'] ?? props.label ?? props.placeholder}
        value={props.value ?? ''}
        onChange={(event) => props.onChange?.(event.target.value === '' ? null : event.target.value)}
      >
        <option value="" />
        {options.map((option) => (
          <option key={option.value} value={option.value}>
            {option.label}
          </option>
        ))}
      </select>
    );
  }

  return { ...actual, Select: StubSelect };
});

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
  nameL10n: { en: 'Industrial Environmental Control (quarterly)' },
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

/** Симулює фонову задачу: рядок з'являється в базі, лише коли задача успішна. */
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

      // ⛔ Головна вісь тесту: у момент відповіді `202` рядка зрізу ЩЕ немає
      // (`state.snapshotBuilt === false`). Лише коли ОПИТУВАННЯ задачі
      // повідомляє про успіх, "сервер" нарешті записує рядок — так само,
      // як у живій системі задача завершується асинхронно.
      if (url.includes('/api/v1/jobs/job-1')) {
        state.snapshotBuilt = true;
        return json({ jobId: 'job-1', state: 'Succeeded', percent: 100, message: null, error: null });
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
});

describe('SnapshotsPage: список оновлюється після завершення побудови (lane6)', () => {
  it('щойно побудований зріз з\'являється в списку без ручного перезавантаження', async () => {
    mockApi();
    await loadCatalog('en', 'private');
    show();

    // ⛔ До побудови список порожній.
    await screen.findByText('No snapshots built yet');

    // Кнопка «Build snapshot» заблокована, доки не обрано проєкт.
    fireEvent.change(await screen.findByLabelText('Project'), {
      target: { value: '1' },
    });

    fireEvent.click(await screen.findByRole('button', { name: 'Build snapshot' }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('Report code'), {
      target: { value: 'IEC' },
    });

    fireEvent.click(within(dialog).getByRole('button', { name: 'Build snapshot' }));

    // ⛔ Мутаційний доказ (RED на невиправленому коді): стара версія
    // інвалідувала список ОДРАЗУ на 202-відповіді (коли рядка зрізу ще
    // немає) і більше НІКОЛИ не перевіряла знову — цей запит завис би на
    // "No snapshots built yet" назавжди.
    await waitFor(() => {
      expect(screen.queryByText('No snapshots built yet')).toBeNull();
    });

    expect(await screen.findByText('42')).toBeDefined();
  });
});
