/**
 * Діалоги життєвого циклу, що живуть в `?dialog=` адреси. Окремий малий модуль: сторінка читає адресу статично, а
 * самі діалоги (`LifecycleDialogs`) — ліниво, тож константи не можуть лежати в чанку діалогів.
 */
export const LifecycleDialogIds = ['activate', 'end', 'renew', 'delete'] as const;
export type LifecycleDialogId = (typeof LifecycleDialogIds)[number];

/** Параметр адреси відкритого діалогу. */
export const DialogParam = 'dialog';

/** Діалог з адреси; невідомий — `null`. */
export function readLifecycleDialog(value: string | null): LifecycleDialogId | null {
  return (LifecycleDialogIds as readonly string[]).includes(value ?? '') ? (value as LifecycleDialogId) : null;
}
