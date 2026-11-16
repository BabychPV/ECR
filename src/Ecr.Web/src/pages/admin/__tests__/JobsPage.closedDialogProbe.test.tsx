import { afterEach, describe, it, vi } from 'vitest';
import { fireEvent, screen, within } from '@testing-library/react';
import { JobsPage } from '@/pages/admin/JobsPage';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';
import { openDrawerOf } from './jobsTestKit';

/**
 * B149, група «null-захист в обробнику»: підтвердження «Cancel job», що закрилося (ще в DOM), не скасовує
 * задачу вдруге (і не скасовує ІНШУ: ціль береться з `confirming`, а не зі шторки).
 *
 * Зонд на СПРАВЖНІЙ сторінці (власник діалогу): вікно 1 — `if (cancel.isPending) return` (спінер
 * `usePendingLoading` ще вимкнений); вікна 2 немає — `onSuccess` нічого не чекає (`void invalidateQueries`),
 * `setConfirming(null)` одразу; вікно 3 — `confirming === null` → обробник нічого не шле. ЗАКРИТО, один
 * `POST /api/v1/jobs/IRecalculationJob%2342/cancel`.
 */

const jobs = [
  {
    jobCode: 'Ecr.Application.Ports.IRecalculationJob',
    jobId: 'IRecalculationJob#42',
    percent: 40,
    state: 'Running',
    updatedAt: '2026-09-19T10:00:00Z',
  },
];

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      userId: 7,
      userName: 'tester',
      permissions: ['System.ViewHealth', 'System.RunJob'],
      denies: [],
      grants: {},
      isSimulation: false,
      language: 'uk',
      mustChangePassword: false,
    };
  }
  if (url.endsWith('/api/v1/jobs')) return jobs;

  return null;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: JobsPage «Cancel job» — одне скасування на підтвердження', () => {
  it('подвійний клік і закритий діалог — рівно один POST …/cancel', async () => {
    const server = holdWrites({ read, write: () => ({ status: 202, body: { jobId: 'IRecalculationJob#42' } }) });
    const gate = gateReads();
    renderKeepMounted(<JobsPage />, '/admin/jobs');

    const drawer = await openDrawerOf('Running');
    fireEvent.click(await within(drawer).findByRole('button', { name: '⟦jobs.cancel⟧' }));
    const dialog = await screen.findByRole('dialog', { name: '⟦jobs.cancel⟧' });
    const confirm = within(dialog).getByRole('button', { name: '⟦jobs.cancel⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => confirm,
      isOpen: () => screen.queryByRole('dialog', { name: '⟦jobs.cancel⟧' }) !== null,
      expected: ['POST /api/v1/jobs/IRecalculationJob%2342/cancel'],
      awaitedRead: null,
    });
  });
});
