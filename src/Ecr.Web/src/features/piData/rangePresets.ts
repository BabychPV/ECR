import type { RawWindow } from './api';

/**
 * Пресети вікна «Дані з PI» (`D-463`).
 *
 * ⚠ Період PI — річний, тому поруч із «останніми N днями» є календарні роки (поточний і попередній). Межі
 * календарного року — у UTC: сутність збору не належить проєкту, і пояс проєкту тут невідомий; межі періоду в
 * поясі проєкту показує комірковий вхід («Звідки це значення»), де проєкт відомий.
 *
 * ⛔ Вікно не довше за 400 діб — стеля сервера (`RawDataQuery.MaxRangeDays`); жоден пресет її не перевищує.
 */
export const RangePresets = ['24h', '7d', '30d', '90d', 'year', 'lastYear'] as const;
export type RangePreset = (typeof RangePresets)[number];

/** Пресет за замовчуванням — як вікно перегляду мапінгу. */
export const DefaultPreset: RangePreset = '7d';

const Day = 24 * 60 * 60 * 1000;

/** Чи рядок — відомий пресет. */
export function isRangePreset(value: string | null): value is RangePreset {
  return value !== null && (RangePresets as readonly string[]).includes(value);
}

/**
 * Межі вікна пресета відносно `now`.
 *
 * ⚠ «Останні N» округлюються вгору до цілої хвилини: інакше кожен рендер давав би нове вікно, новий ключ
 * запиту і нескінченні перезапити.
 */
export function presetWindow(preset: RangePreset, now: Date): RawWindow {
  const minute = Math.ceil(now.getTime() / 60_000) * 60_000;
  const year = now.getUTCFullYear();

  switch (preset) {
    case '24h':
      return span(minute - Day, minute);
    case '7d':
      return span(minute - 7 * Day, minute);
    case '30d':
      return span(minute - 30 * Day, minute);
    case '90d':
      return span(minute - 90 * Day, minute);
    case 'year':
      return span(Date.UTC(year, 0, 1), Math.min(Date.UTC(year + 1, 0, 1), minute));
    case 'lastYear':
      return span(Date.UTC(year - 1, 0, 1), Date.UTC(year, 0, 1));
  }
}

function span(from: number, to: number): RawWindow {
  return { fromUtc: new Date(from).toISOString(), toUtc: new Date(Math.max(to, from + 60_000)).toISOString() };
}
