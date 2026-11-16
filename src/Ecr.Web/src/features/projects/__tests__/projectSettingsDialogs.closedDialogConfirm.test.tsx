import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import type { PeriodCalendarDto } from '@/api/types';
import { ApprovalRouteEditor } from '@/features/projects/ApprovalRouteEditor';
import { CurrentPeriodSetting } from '@/features/projects/CurrentPeriodSetting';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B145b: діалоги налаштувань проєкту «Pin current period» і «Approval route», що вже закрилися, не
 * зберігають удруге.
 *
 * ⛔ Діра (зонд на базі d618ea1f6, справжні компоненти): після відповіді сервера діалог закрито, але
 * власний `Modal` ще ~200 мс у DOM, поля ті самі, кнопка активна. Другий клік подвійного кліку слав другий
 * `PUT` того самого об'єкта з тим самим тілом (зонд: по 2 запити замість 1) — повторне закріплення періоду
 * з тією самою причиною / повторна повна заміна маршруту погодження, без підтвердження.
 *
 * Захист — у самих компонентах: кнопка діє лише у відкритому діалозі й не під час запиту.
 *
 * Мутаційні докази (прибрати `!pinning ||` / `!opened ||`):
 * - `CurrentPeriodSetting.tsx` → червоний «Pin»
 *   (`expected [ …(2) ] to deeply equal [ 'PUT /api/v1/projects/7/settings/current-period' ]`);
 * - `ApprovalRouteEditor.tsx` → червоний «Approval route»
 *   (`expected [ …(2) ] to deeply equal [ 'PUT /api/v1/projects/7/approval-route' ]`).
 */

const Calendar = { periodKind: 'Monthly', periods: [{ id: 1, periodKey: 202601 }] } as unknown as PeriodCalendarDto;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: діалоги налаштувань проєкту, що закрилися (ще в DOM), не зберігають удруге', () => {
  it('Pin: один PUT; клік по «Pin» закритого діалогу нічого не шле', async () => {
    const server = holdWrites({
      read: (url) =>
        url.includes('/settings/current-period')
          ? { mode: 'Auto', currentPeriodKey: 202601, automaticPeriodKey: 202601, pinnedPeriodId: null, pinnedReason: null }
          : {},
      write: () => ({
        body: { mode: 'Pinned', currentPeriodKey: 202601, automaticPeriodKey: 202601, pinnedPeriodId: 1, pinnedReason: 'year-end close' },
      }),
    });
    renderKeepMounted(<CurrentPeriodSetting projectId={7} canConfigure calendar={Calendar} />);

    fireEvent.click(await screen.findByRole('button', { name: '⟦projectSettings.pinAction⟧' }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByRole('textbox', { name: /projectSettings\.pinPeriod/ }));
    const option = (await screen.findAllByRole('option'))[0];
    if (option === undefined) throw new Error('у переліку періодів немає опцій');
    fireEvent.click(option);
    fireEvent.change(within(dialog).getByRole('textbox', { name: /workflow\.reason/ }), { target: { value: 'year-end close' } });

    const pin = within(dialog).getByRole('button', { name: '⟦periods.pin⟧' });
    await waitFor(() => {
      expect(pin).toHaveProperty('disabled', false);
    });
    fireEvent.click(pin);
    await waitFor(() => {
      expect(server.sent).toEqual(['PUT /api/v1/projects/7/settings/current-period']);
    });

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(pin).toHaveProperty('disabled', false);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(pin.isConnected).toBe(true);
    fireEvent.click(pin);
    await settle();
    expect(server.sent).toEqual(['PUT /api/v1/projects/7/settings/current-period']);
  });

  it('Approval route: один PUT; клік по «Save» закритого діалогу нічого не шле', async () => {
    const server = holdWrites({
      read: (url) => (url.includes('/approval-route') ? { steps: [] } : url.endsWith('/api/v1/roles') ? [] : {}),
      write: () => ({ body: { steps: 0 } }),
    });
    renderKeepMounted(<ApprovalRouteEditor projectId={7} />);

    fireEvent.click(screen.getByRole('button', { name: '⟦workflow.route⟧' }));
    const dialog = await screen.findByRole('dialog');
    const save = within(dialog).getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['PUT /api/v1/projects/7/approval-route']);
    });

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(save).toHaveProperty('disabled', false);
    });

    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['PUT /api/v1/projects/7/approval-route']);
  });
});
