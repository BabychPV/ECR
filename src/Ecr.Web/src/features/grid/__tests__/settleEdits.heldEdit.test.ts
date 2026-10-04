import { afterEach, describe, expect, it, vi } from 'vitest';
import { notifications } from '@mantine/notifications';
import { cancelAutosave } from '../autosave';
import { firstHeldEdit, markPendingRejected, putPendingEdit, resetPending } from '../pendingStore';
import { registerHeldEditRevealer, whenEditsSaved } from '../settleEdits';
import type { PendingEdit } from '../useCellPatch';

/**
 * AN-28 P2-1: одна утримана (відхилена сервером) комірка блокувала Submit/Approve/Apply
 * і - даремно - Validate/Export, а відмова приходила загальним тостом лише через 3 с
 * (таймаут очікування «порожнього сховища», якого з утриманою правкою не буває).
 * Тепер: відмова одразу, з причиною сервера, і комірка показується; дії на читання
 * йдуть по збереженому з попередженням.
 */

vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

const held: PendingEdit = { rowKey: 'r1', columnCode: 'C1', value: 'abc', isEmpty: false, baseVersion: 'v1' };

function holdOneCell(): void {
  putPendingEdit(4, 202609, held);
  markPendingRejected(4, 202609, [{ edit: held, message: 'Value must be a number', scope: 'cell' }]);
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.mocked(notifications.show).mockClear();
});

describe('AN-28 P2-1: утримана комірка і дії над документом', () => {
  it('Submit блокується одразу (не через 3 с), з причиною сервера, комірку показано', async () => {
    holdOneCell();
    const reveal = vi.fn();
    const off = registerHeldEditRevealer(reveal);
    const action = vi.fn();

    const started = Date.now();
    const done = await whenEditsSaved(action);
    off();

    expect(Date.now() - started).toBeLessThan(1000);
    expect(done).toBe(false);
    expect(action).not.toHaveBeenCalled();
    expect(notifications.show).toHaveBeenCalledWith(
      expect.objectContaining({ color: 'statusError', message: 'Value must be a number' }),
    );
    expect(reveal).toHaveBeenCalledWith(
      expect.objectContaining({ tableInstanceId: 4, periodKey: 202609, edit: expect.objectContaining({ rowKey: 'r1', columnCode: 'C1' }) }),
    );
  });

  it('Validate/Export (readOnly) виконуються по збереженому з попередженням', async () => {
    holdOneCell();
    const reveal = vi.fn();
    const off = registerHeldEditRevealer(reveal);
    const action = vi.fn();

    const done = await whenEditsSaved(action, { readOnly: true });
    off();

    expect(done).toBe(true);
    expect(action).toHaveBeenCalledTimes(1);
    expect(notifications.show).toHaveBeenCalledWith(expect.objectContaining({ color: 'statusWarning' }));
    expect(reveal).toHaveBeenCalledTimes(1);
  });

  it('незбереженого немає: дія виконується без жодного тосту', async () => {
    const action = vi.fn();

    expect(await whenEditsSaved(action)).toBe(true);
    expect(action).toHaveBeenCalledTimes(1);
    expect(notifications.show).not.toHaveBeenCalled();
  });

  it('виправлена комірка (нове значення) вже не утримана', () => {
    holdOneCell();
    putPendingEdit(4, 202609, { ...held, value: 5 });

    expect(firstHeldEdit()).toBeNull();
  });
});
