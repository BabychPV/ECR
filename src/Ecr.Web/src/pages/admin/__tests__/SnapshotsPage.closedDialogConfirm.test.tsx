import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import type { PagedProjects, ReportDefinition } from '@/api/types';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { SnapshotsPage } from '../SnapshotsPage';

/**
 * B145b: діалог «Build snapshot», що вже закрився, не ставить другу побудову зрізу.
 *
 * ⛔ Діра (зонд на базі d618ea1f6, справжня сторінка): постановка відповіла `202` — діалог закрито
 * (`building = false`), але власний `Modal` ще ~200 мс у DOM, звіт і період ті самі, «Build snapshot»
 * активна. Другий клік подвійного кліку ставив ДРУГУ задачу побудови того самого зрізу (другий
 * `POST …/build`), а `jobId` першої затирався. Зрізи незмінні й не видаляються — зайвий лишається назавжди.
 *
 * Захист — у самій сторінці: кнопка діє лише у відкритому діалозі й не під час запиту.
 *
 * Мутаційний доказ: прибрати `!building ||` у `SnapshotsPage.tsx` → червоний
 * (`expected [ 'POST /api/v1/reports/IEC/build', …(1) ] to deeply equal [ 'POST /api/v1/reports/IEC/build' ]`).
 */

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
  nameL10n: { values: { en: 'Industrial Environmental Control' } },
  versions: [{ id: 1, columnsJson: '[]', rulesJson: '[]', status: 'Published', createdAt: '2026-01-01T00:00:00Z' }],
} as unknown as ReportDefinition;

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      userId: 1,
      userName: 'bootstrap',
      language: 'en',
      permissions: ['Report.BuildSnapshot', 'Report.EditDefinition'],
      isSimulation: false,
      denies: [],
      grants: {},
      mustChangePassword: false,
      simulatedForUserId: null,
    };
  }
  if (url.includes('/api/v1/projects')) return { items: [project], nextCursor: null, totalCount: 1 } satisfies PagedProjects;
  if (url.includes('/api/v1/reports/snapshots')) return [];
  // Задача ще виконується: опитування стану нічого не завершує.
  if (url.includes('/api/v1/jobs/')) return { jobId: 'job-1', state: 'Running', percent: 10, message: null, error: null };
  if (url.includes('/api/v1/reports')) return [reportDef];
  if (url.endsWith('/api/v1/languages')) return [];

  return {};
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: Build snapshot, що закрився (ще в DOM), не ставить другу побудову', () => {
  it('одна постановка; клік по «Build snapshot» закритого діалогу нічого не шле', async () => {
    const server = holdWrites({ read, write: () => ({ status: 202, body: { jobId: 'job-1' } }) });
    renderKeepMounted(<SnapshotsPage />);

    fireEvent.click(await screen.findByLabelText('⟦documents.project⟧'));
    fireEvent.click(await screen.findByRole('option', { name: 'AUDIT_SMOKE_PRJ' }));
    fireEvent.click(await screen.findByRole('button', { name: '⟦snapshots.build⟧' }));

    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByLabelText('⟦snapshots.code⟧'));
    fireEvent.click(await screen.findByRole('option', { name: 'Industrial Environmental Control (IEC)' }));

    const build = within(dialog).getByRole('button', { name: '⟦snapshots.build⟧' });
    fireEvent.click(build);
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/reports/IEC/build']);
    });

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(build).toHaveProperty('disabled', false);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(build.isConnected).toBe(true);
    fireEvent.click(build);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/reports/IEC/build']);
  });
});
