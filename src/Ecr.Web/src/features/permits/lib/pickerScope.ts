import type { PermitColumn } from './columns';

/**
 * Три області списків вибору (PM-06). Дзеркало `scopeOf` / `choicesOf` / `optsFor` мокапу
 * (`docs/design/permits/screens-permits.js`, README §2a):
 *
 * | Область    | Звідки значення                                     |
 * |------------|-----------------------------------------------------|
 * | `shared`   | звичайний реєстр із «Registries»                    |
 * | `type`     | список лише цього типу перміта (стовпець `choice`)  |
 * | `permit`   | рядки іншої таблиці ТОГО САМОГО перміта             |
 */
export type PickerScope = 'shared' | 'type' | 'permit';

/** Ознака у `ref`: таблиця того самого перміта (`StoragePlaces (this permit)`). */
const ThisPermitMarker = /\(this permit\)/i;

/** Один варіант вибору. */
export interface PickerOption {
  readonly value: string;
  readonly label: string;
}

/**
 * Джерела значень для пікерів. Ключ — код реєстру (`Substances`) чи таблиці перміта
 * (`StoragePlaces`); значення — вже готові підписи. Самі дані приходять з API на наступному кроці,
 * тут лише модель вибору.
 */
export interface PickerSources {
  readonly shared?: Readonly<Record<string, readonly string[]>> | undefined;
  readonly permitTables?: Readonly<Record<string, readonly string[]>> | undefined;
}

/** Область списку стовпця. Тільки `choice` — `type`; `link` — `permit` або `shared` за `ref`. */
export function scopeOf(column: Pick<PermitColumn, 'type' | 'ref'>): PickerScope {
  if (column.type === 'choice') return 'type';
  return column.ref !== undefined && ThisPermitMarker.test(column.ref) ? 'permit' : 'shared';
}

/** Код реєстру/таблиці з `ref`: перше слово (`StoragePlaces (this permit)` → `StoragePlaces`). */
export function refCode(column: Pick<PermitColumn, 'ref'>): string | null {
  const first = (column.ref ?? '').trim().split(/\s+/)[0] ?? '';
  return first === '' ? null : first;
}

/** Значення списку «лише цього типу». Порожньо, якщо їх не задано (вигаданих значень немає). */
export function choicesOf(column: Pick<PermitColumn, 'choices'>): readonly string[] {
  return column.choices ?? [];
}

/**
 * Варіанти пікера стовпця. Дублікати й порожні підписи відкидаються, порядок джерела зберігається.
 * Невідомий реєстр/таблиця — порожній список, а не «Item 1, Item 2», як у демо мокапу.
 */
export function optionsFor(
  column: Pick<PermitColumn, 'type' | 'ref' | 'choices'>,
  sources: PickerSources = {},
): PickerOption[] {
  const scope = scopeOf(column);
  let labels: readonly string[];

  if (scope === 'type') {
    labels = choicesOf(column);
  } else {
    const code = refCode(column);
    const pool = scope === 'permit' ? sources.permitTables : sources.shared;
    labels = code === null ? [] : (pool?.[code] ?? []);
  }

  const seen = new Set<string>();
  const options: PickerOption[] = [];
  for (const raw of labels) {
    const label = raw.trim();
    if (label === '' || seen.has(label)) continue;
    seen.add(label);
    options.push({ value: label, label });
  }
  return options;
}

/**
 * Чи можна додати значення «на льоту» з форми рядка: у спільний реєстр і у список типу — так
 * (для спільного потрібне право запису в реєстр, перевіряє викликач), у таблицю того самого
 * перміта — ні: це рядки іншої таблиці, їх додають там.
 */
export function canAddValueInline(scope: PickerScope): boolean {
  return scope !== 'permit';
}
