/**
 * Частота синку подій PI (Event Frames) — Q-2, рішення людини 06.11: «це має бути параметр налаштування в UI».
 *
 * ⚠ Частота — це той самий розклад збору сутності (`ext.CollectionSchedule`, cron Quartz), що й на вкладці «Розклад»;
 * тут лише набір меж 5 хв … 24 год замість вільного cron: повна звірка за період важча за добірку нових, тож частіше за
 * 5 хв не треба, а рідше за добу — дані запізнюються на день. Cron поза набором показується як «Інший» і не
 * затирається, доки людина сама не вибере частоту.
 */
export interface EventFrequency {
  /** Ключ варіанта (і значення `Select`); підпис — `frequencyLabel` у `SourceEventFrequency.tsx` (літерали ключів). */
  readonly value: string;
  /** Вираз cron Quartz (6 полів). */
  readonly cron: string;
}

/** Типова частота нового шаблону подій — щогодини (дзеркало `CreateSourceEventMapHandler.DefaultEventSyncCron`). */
export const DefaultEventFrequency = 'PT1H';

export const EventFrequencies: readonly EventFrequency[] = [
  { value: 'PT5M', cron: '0 0/5 * * * ?' },
  { value: 'PT15M', cron: '0 0/15 * * * ?' },
  { value: 'PT30M', cron: '0 0/30 * * * ?' },
  { value: 'PT1H', cron: '0 0 * * * ?' },
  { value: 'PT2H', cron: '0 0 0/2 * * ?' },
  { value: 'PT6H', cron: '0 0 0/6 * * ?' },
  { value: 'PT12H', cron: '0 0 0/12 * * ?' },
  { value: 'P1D', cron: '0 0 0 * * ?' },
];

/** Значення варіанта для cron розкладу; `null` — cron поза набором (заданий на вкладці «Розклад»). */
export function frequencyOfCron(cron: string): string | null {
  const normalized = cron.trim().replace(/\s+/g, ' ');
  return EventFrequencies.find((item) => item.cron === normalized)?.value ?? null;
}

/** Cron варіанта; невідоме значення — `undefined`. */
export function cronOfFrequency(value: string): string | undefined {
  return EventFrequencies.find((item) => item.value === value)?.cron;
}
