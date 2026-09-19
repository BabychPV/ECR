import type { ReactNode } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { theme } from '@/shared/theme/theme';
import { TwoLine, hasContent } from '@/shared/ui/TwoLine';

/**
 * `UI-04`: два рядки в комірці, і `D15-06` — елемент без даних не малюється.
 */

/**
 * ⚠ Вміст монтується у власний вузол-пробу: `MantineProvider` кладе поруч
 * `<style>` із усією темою, і `container.textContent` після цього містить
 * двадцять кілобайтів CSS, а не текст компонента — саме те, що цей файл
 * перевіряє на порожнечу.
 */
function show(node: ReactNode): HTMLElement {
  const { container } = render(
    <MantineProvider theme={theme}>
      <div data-probe="">{node}</div>
    </MantineProvider>,
  );

  const probe = container.querySelector<HTMLElement>('[data-probe]');
  if (probe === null) throw new Error('пробний вузол не змонтувався');

  return probe;
}

afterEach(cleanup);

describe('TwoLine малює те, що є', () => {
  it('обидва рядки', () => {
    show(<TwoLine primary="Excel export" secondary="J-10427" />);

    expect(screen.getByText('Excel export')).toBeDefined();
    expect(screen.getByText('J-10427')).toBeDefined();
  });

  it('моноширинний другий рядок — за пропом, не завжди', () => {
    const container = show(<TwoLine primary="Air emissions" secondary="B12" mono />);
    const code = container.querySelector('[data-two-line-secondary]');

    expect(code?.getAttribute('style') ?? '').toContain('monospace');
  });

  it('без mono другий рядок моноширинним не стає', () => {
    const container = show(<TwoLine primary="Air emissions" secondary="B12" />);
    const code = container.querySelector('[data-two-line-secondary]');

    expect(code?.getAttribute('style') ?? '').not.toContain('monospace');
  });
});

describe('D15-06: елемент без даних не малюється', () => {
  /*
   * ⛔ Перевіряється НЕ «прочерк на місці», а ВІДСУТНІСТЬ вузла. Прочерк —
   * теж заглушка: він займає рядок, читається скрінрідером і не
   * відрізняється від справжнього значення «—» у даних.
   */
  it('порожній другий рядок не лишає ні «—», ні порожнього вузла', () => {
    const container = show(<TwoLine primary="Excel export" secondary="" />);

    expect(screen.getByText('Excel export')).toBeDefined();
    expect(container.querySelector('[data-two-line-secondary]')).toBeNull();
    expect(container.textContent).toBe('Excel export');
  });

  it('другий рядок із самих пробілів — так само порожній', () => {
    const container = show(<TwoLine primary="Excel export" secondary="   " />);

    expect(container.querySelector('[data-two-line-secondary]')).toBeNull();
  });

  it('порожній ПЕРШИЙ рядок теж зникає, а не з’їдає другий', () => {
    const container = show(<TwoLine secondary="J-10427" />);

    expect(container.querySelector('[data-two-line-primary]')).toBeNull();
    expect(screen.getByText('J-10427')).toBeDefined();
  });

  it('немає нічого — немає й обгортки: жодного вузла-привида', () => {
    const container = show(<TwoLine primary={null} secondary={undefined} />);

    expect(container.querySelector('[data-two-line]')).toBeNull();
    expect(container.textContent).toBe('');
  });
});

describe('hasContent: що вважається порожнечею (D15-06)', () => {
  it.each([null, undefined, false, '', '   ', []])('порожнє: %o', (value) => {
    expect(hasContent(value as ReactNode)).toBe(false);
  });

  /*
   * ⛔ `0` — це ДАНІ, а не порожнеча. Зворотне трактування і є тим дефектом,
   * від якого застерігає `D15-06`: «0 зауважень» у перевіреного документа —
   * змістовна відповідь, і сховати її означає збрехати мовчанням.
   */
  it.each([0, '0', 'x', ['a']])('не порожнє: %o', (value) => {
    expect(hasContent(value as ReactNode)).toBe(true);
  });

  it('масив із самих порожніх елементів порожній, з одним непорожнім — ні', () => {
    expect(hasContent(['', null, '  '])).toBe(false);
    expect(hasContent(['', 'B12'])).toBe(true);
  });
});
