import type { NotificationRule } from '@/features/notifications/api';

/** Подія матриці правил — рівно перелік сервера. */
type EventKind = NotificationRule['eventKind'];

/** Подія, текст якої живе в каталозі рядків і правиться адміністратором. */
export interface TemplatedEvent {
  readonly eventKind: EventKind;
  /** Ключ теми листа в каталозі. */
  readonly subjectKey: string;
  /** Ключ тіла листа в каталозі. */
  readonly bodyKey: string;
}

/**
 * Події з власним шаблоном листа (`AN-9`, `PeriodStateJob.NotifyAsync`).
 *
 * ⛔ Перелік виписаний тут, а не зібраний із `eventKinds` сервера: решта подій (збої задач, збору,
 * експорту, партицій) іде ЗВЕДЕННЯМ збоїв (`NotificationJob`), і окремого шаблону з плейсхолдерами в
 * них немає. Показати для них порожню форму означало б пообіцяти правку, яка нічого не змінить.
 *
 * ⚠ Ключі — ті самі літерали, що передає сервер (`PeriodStateJob.cs`). Зміниться ключ там — цей
 * перелік покаже «шаблону немає в каталозі», а не мовчки правитиме мертвий рядок.
 */
export const TemplatedEvents = [
  {
    eventKind: 'PeriodOpened',
    subjectKey: 'notifications.periodOpened.subject',
    bodyKey: 'notifications.periodOpened.body',
  },
  {
    eventKind: 'PeriodGraceStarted',
    subjectKey: 'notifications.periodGraceStarted.subject',
    bodyKey: 'notifications.periodGraceStarted.body',
  },
] as const satisfies readonly TemplatedEvent[];

/**
 * Підстановки, які сервер заповнює в шаблонах подій періоду (`PeriodStateJob.NotifyAsync`:
 * `{project}` — код проєкту, `{period}` — ключ і межі періоду). Будь-яка інша лишилася б у листі
 * фігурними дужками.
 */
export const TemplatePlaceholders = ['period', 'project'] as const;

/**
 * Набір плейсхолдерів тексту — без повторів, упорядкований.
 *
 * ⚠ Рівно правило сервера (`UiStringResolver.Placeholders`, шаблон `\{(\w+)\}`, порядок ordinal):
 * інакше клієнт пропустив би те, що сервер відхилить `422`, або навпаки.
 */
export function placeholdersOf(text: string): string[] {
  const found = new Set<string>();

  for (const match of text.matchAll(/\{(\w+)\}/g)) {
    if (match[1] !== undefined) found.add(match[1]);
  }

  return [...found].sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

/** Чому текст шаблону не можна зберегти; `null` — можна. */
export type TemplateProblem =
  | { readonly kind: 'empty' }
  | { readonly kind: 'unknownPlaceholder'; readonly names: string[] }
  | {
      readonly kind: 'placeholderMismatch';
      readonly expected: string[];
      readonly actual: string[];
    };

/**
 * Перевірка одного поля шаблону до запису.
 *
 * ⛔ Мова за замовчуванням — еталон: порожньою вона бути не може (лист без теми), а
 * плейсхолдери в ній мусять бути з тих, що сервер заповнює. Мова перекладу може бути
 * порожньою — це «зняти переклад», лист піде мовою за замовчуванням; непорожня мусить нести
 * РІВНО набір еталона — те саме правило, яким сервер відповідає `422 placeholderMismatch`.
 *
 * @param value Введений текст.
 * @param reference Текст мовою за замовчуванням.
 * @param isDefault Чи редагується мова за замовчуванням.
 */
export function templateProblem(value: string, reference: string, isDefault: boolean): TemplateProblem | null {
  if (value.trim().length === 0) return isDefault ? { kind: 'empty' } : null;

  const actual = placeholdersOf(value);
  const unknown = actual.filter((name) => !(TemplatePlaceholders as readonly string[]).includes(name));
  if (unknown.length > 0) return { kind: 'unknownPlaceholder', names: unknown };

  if (!isDefault) {
    const expected = placeholdersOf(reference);
    if (expected.join(',') !== actual.join(',')) return { kind: 'placeholderMismatch', expected, actual };
  }

  return null;
}
