import { afterEach, describe, it, vi } from 'vitest';
import { fireEvent, screen, within } from '@testing-library/react';
import { PeriodsPage } from '@/pages/admin/PeriodsPage';
import { openPeriodDetails } from './periodDetailsOpen';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149, група «null-захист в обробнику»: «Reopen period», що закрився (ще в DOM) або ще чекає перечитування,
 * не відкриває період удруге.
 *
 * Зонд на СПРАВЖНІЙ сторінці (власник): вікно 2 — `await refresh()` (проєкти, потім календар), діалог
 * відкритий, `loading={reopenPeriod.isPending}` (вимкнена); вікно 3 — `reopening === null` → обробник нічого
 * не шле (причина в стані лишається, тож кнопка НЕ вимкнена — тримає саме null-захист). ЗАКРИТО, один
 * `POST /api/v1/periods/42/reopen`.
 */

const calendar = {
  projectId: 7,
  periodKind: 'Monthly',
  currentPeriodMode: 'Auto',
  timeZoneId: 'Asia/Aqtau',
  policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 },
  periods: [
    {
      id: 42,
      periodKey: 202601,
      sequence: 1,
      startsAt: '2026-01-01',
      endsAt: '2026-02-01',
      state: 'Closed',
      graceEndsAt: null,
      reopenedUntil: null,
      isCurrent: false,
    },
  ],
};

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      denies: [],
      grants: { 'Project:7': 'Manage' },
      isSimulation: false,
      language: 'en',
      mustChangePassword: false,
      permissions: ['Period.Reopen'],
      simulatedForUserId: null,
      userId: 1,
      userName: 'tester',
    };
  }
  if (url.endsWith('/api/v1/projects/7/periods')) return calendar;
  if (url.endsWith('/api/v1/projects')) {
    return {
      items: [{ id: 7, code: 'PRJ-7', status: 'Active', periodKind: 'Monthly', periodCount: 1, currentPeriodId: null, timeZoneId: 'Asia/Aqtau' }],
      nextCursor: null,
      totalCount: 1,
    };
  }

  return null;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: PeriodsPage «Reopen period» — одне перевідкриття на підтвердження', () => {
  it('подвійний клік, очікування перечитування і закритий діалог — рівно один POST …/reopen', async () => {
    const server = holdWrites({ read });
    const gate = gateReads();
    openPeriodDetails();
    renderKeepMounted(<PeriodsPage projectId={7} />, '/admin/periods?projectId=7');

    fireEvent.click(await screen.findByRole('button', { name: /⟦periods\.reopen⟧/ }, { timeout: 10_000 }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText(/⟦workflow\.reason⟧/), { target: { value: 'Уточнення даних за січень' } });
    const buttons = within(dialog).getAllByRole('button', { name: /⟦periods\.reopen⟧/ });
    const confirm = buttons[buttons.length - 1] as HTMLElement;

    await probeThreeWindows({
      server,
      gate,
      button: () => confirm,
      isOpen: () => screen.queryByRole('dialog') !== null,
      expected: ['POST /api/v1/periods/42/reopen'],
      awaitedRead: '/api/v1/projects',
    });
  });
});
