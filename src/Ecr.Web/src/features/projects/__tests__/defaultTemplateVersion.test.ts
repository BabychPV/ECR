import { describe, expect, it } from 'vitest';
import {
  pickDefaultVersionId,
  type PublishedVersionOption,
} from '@/features/projects/defaultTemplateVersion';

/**
 * ФВ-1.2 (рішення людини 12.10): форма створення проєкту пропонує ОСТАННЮ ОПУБЛІКОВАНУ версію шаблону.
 *
 * Чиста функція, а не рендер: форма в jsdom рендериться хвилинами (див. `create-project.test.tsx`), а вибір
 * «яка саме» — єдине, що тут може бути хибним. Проводку в компонент доводить e2e `projectDefaultVersionLive.spec.ts`.
 */
function option(
  id: number,
  publishedAt: string | null,
  overrides: Partial<PublishedVersionOption> = {},
): PublishedVersionOption {
  return { value: String(id), label: `T · ${String(id)}`, publishedAt, archived: false, ...overrides };
}

describe('Версія шаблону за замовчуванням у формі проєкту (ФВ-1.2)', () => {
  it('після публікації v2 пропонується v2, а не перша в переліку', () => {
    const v1 = option(10, '2026-09-01T10:00:00Z');
    const v2 = option(11, '2026-10-01T10:00:00Z');

    // ⛔ Перелік сервера впорядкований за Id зростаючи: «перший елемент» дав би v1.
    expect(pickDefaultVersionId([v1, v2])).toBe('11');
    // Порядок вхідного переліку не впливає на результат.
    expect(pickDefaultVersionId([v2, v1])).toBe('11');
  });

  it('обирає за часом публікації, а не за номером версії чи Id', () => {
    // v3 (id 12) опубліковано РАНІШЕ за «1.1» (id 11): хронологія публікації, а не рядок номера.
    const older = option(12, '2026-09-10T00:00:00Z');
    const newer = option(11, '2026-10-05T00:00:00Z');

    expect(pickDefaultVersionId([older, newer])).toBe('11');
  });

  it('за однакового моменту публікації бере більший Id', () => {
    const a = option(5, '2026-10-01T00:00:00Z');
    const b = option(9, '2026-10-01T00:00:00Z');

    expect(pickDefaultVersionId([b, a])).toBe('9');
  });

  it('версія без часу публікації програє будь-якій датованій', () => {
    expect(
      pickDefaultVersionId([option(20, null), option(3, '2026-01-01T00:00:00Z')]),
    ).toBe('3');
  });

  it('архівні шаблони не пропонуються, навіть коли їхня версія найновіша', () => {
    const live = option(1, '2026-09-01T00:00:00Z');
    const archived = option(2, '2026-10-10T00:00:00Z', { archived: true });

    expect(pickDefaultVersionId([live, archived])).toBe('1');
  });

  it('немає опублікованих (або лише архівні) — пропозиції немає', () => {
    expect(pickDefaultVersionId([])).toBeNull();
    expect(pickDefaultVersionId([option(2, '2026-10-10T00:00:00Z', { archived: true })])).toBeNull();
  });
});
