import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { ProjectSettingsPage } from '@/pages/admin/ProjectSettingsPage';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149, група «скидання після успіху → кнопка вимкнена»: «Change time zone» чернетки проєкту, що закрився
 * (ще в DOM) або ще чекає перечитування, не шле `PUT …/timezone` удруге.
 *
 * Зонд на СПРАВЖНІЙ сторінці налаштувань (власник діалогу): вікно 2 — `await refresh()` (проєкти, потім
 * календар), діалог відкритий, `loading={changeTimeZone.isPending}` (вимкнена); вікно 3 —
 * `newTimeZoneId = null` → `disabled`, обробник теж мовчить (`if (newTimeZoneId !== null)`). ЗАКРИТО.
 *
 * F13: шлях «вибрано, Cancel» на базі 0c1ec8ad9 був ВІДКРИТИЙ — «Cancel» пояс не скидає, діалог змонтований завжди,
 * клік по кнопці закритого `Modal` слав `PUT …/timezone`. Захист у обробнику: `if (!changingTimeZone || …) return`.
 * Мутаційний доказ: без `!changingTimeZone ||` — червоний другий тест (`expected [ 'PUT …/timezone' ] to deeply equal []`).
 *
 * ⚠ Перелік поясів підмінено одним значенням (рендер ~600 опцій у jsdom — хвилини).
 */

vi.mock('@/features/projects/timeZones', () => ({ timeZones: () => ['Asia/Atyrau'] }));

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      userId: 1,
      userName: 'admin',
      language: 'en',
      permissions: ['Project.Manage'],
      isSimulation: false,
      denies: [],
      grants: { 'Project:7': 'Manage' },
      mustChangePassword: false,
      simulatedForUserId: null,
    };
  }
  if (url.endsWith('/api/v1/projects')) {
    return {
      items: [{ id: 7, code: 'PRJ-7', status: 'Draft', periodKind: 'Monthly', periodCount: 12, currentPeriodId: null, timeZoneId: 'Asia/Aqtau' }],
      nextCursor: null,
      totalCount: 1,
    };
  }
  if (url.endsWith('/api/v1/projects/7/periods')) {
    return { projectId: 7, periodKind: 'Monthly', currentPeriodMode: 'Auto', timeZoneId: 'Asia/Aqtau', policy: { id: 1, code: 'ECR-Standard', openOffsetDays: 0, graceOffsetDays: 15, hardCloseOffsetDays: 45, yearGraceOffsetDays: 45 }, periods: [] };
  }

  return null;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: ProjectSettingsPage — один PUT …/timezone на відкриття', () => {
  it('подвійний клік, очікування перечитування і закритий діалог — рівно один PUT', async () => {
    const server = holdWrites({ read, write: () => ({ status: 204 }) });
    const gate = gateReads();
    renderKeepMounted(<ProjectSettingsPage projectId={7} />, '/admin/projects?projectId=7');

    fireEvent.click(await screen.findByRole('button', { name: '⟦periods.timezoneChange⟧' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('textbox', { name: /periods\.timeZone/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'Asia/Atyrau', hidden: true }));
    const change = within(dialog).getByRole('button', { name: '⟦periods.timezoneChange⟧' });
    await waitFor(() => {
      expect(change).toHaveProperty('disabled', false);
    });

    await probeThreeWindows({
      server,
      gate,
      button: () => change,
      isOpen: () => screen.queryByRole('dialog') !== null,
      expected: ['PUT /api/v1/projects/7/timezone'],
      awaitedRead: '/api/v1/projects',
    });
    expect(server.bodies).toEqual([{ timeZoneId: 'Asia/Atyrau' }]);
  });

  it('пояс вибрано, «Cancel» — кнопка закритого діалогу (ще в DOM) нічого не шле', async () => {
    const server = holdWrites({ read, write: () => ({ status: 204 }) });
    renderKeepMounted(<ProjectSettingsPage projectId={7} />, '/admin/projects?projectId=7');

    fireEvent.click(await screen.findByRole('button', { name: '⟦periods.timezoneChange⟧' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('textbox', { name: /periods\.timeZone/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'Asia/Atyrau', hidden: true }));
    const change = within(dialog).getByRole('button', { name: '⟦periods.timezoneChange⟧' });
    await waitFor(() => {
      expect(change).toHaveProperty('disabled', false);
    });

    fireEvent.click(within(dialog).getByRole('button', { name: /common\.cancel/ }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });
    expect(change.isConnected).toBe(true);
    fireEvent.click(change);
    await settle();
    expect(server.sent).toEqual([]);
  });
});
