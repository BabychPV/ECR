import type { ReactNode } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { theme } from '@/shared/theme/theme';
import { KeyValue } from '@/shared/ui/KeyValue';

/**
 * `UI-04`: перелік «підпис → значення» і `D15-06` — пара без даних не
 * малюється.
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

describe('KeyValue малює пари', () => {
  it('підпис і значення — списком означень, а не набором div', () => {
    const container = show(
      <KeyValue
        items={[
          { label: 'Started by', value: 'D. Akhmetova' },
          { label: 'Attempts', value: '3' },
        ]}
      />,
    );

    // ⚠ Саме `dl`/`dt`/`dd`: скрінрідер оголошує список означень і дозволяє
    // стрибати між парами, а однакові `div` читає суцільним текстом.
    expect(container.querySelector('dl')).not.toBeNull();
    expect(container.querySelectorAll('dt')).toHaveLength(2);
    expect(container.querySelectorAll('dd')).toHaveLength(2);
    expect(screen.getByText('D. Akhmetova').tagName.toLowerCase()).toBe('dd');
  });

  it('значення-вузол (не текст) проходить як є', () => {
    show(<KeyValue items={[{ label: 'State', value: <span>Running</span> }]} />);

    expect(screen.getByText('Running')).toBeDefined();
  });

  it('mono вмикається пропом і лише для своєї пари', () => {
    const container = show(
      <KeyValue
        items={[
          { label: 'Job', value: 'J-10427', mono: true },
          { label: 'Started by', value: 'D. Akhmetova' },
        ]}
      />,
    );

    const cells = container.querySelectorAll('dd');

    expect(cells[0]?.getAttribute('style') ?? '').toContain('monospace');
    expect(cells[1]?.getAttribute('style') ?? '').not.toContain('monospace');
  });

  it('wide не змінює змісту — лише розкладку', () => {
    show(<KeyValue wide items={[{ label: 'Attempts', value: '3' }]} />);

    expect(screen.getByText('Attempts')).toBeDefined();
    expect(screen.getByText('3')).toBeDefined();
  });
});

describe('D15-06: пара без значення не малюється', () => {
  /*
   * ⛔ Перевіряється саме ВІДСУТНІСТЬ підпису, а не «замість значення стоїть
   * прочерк». Прочерк у шторці читається як «сервер відповів, і там
   * порожньо», тоді як поля може не бути у відповіді взагалі — і саме цю
   * різницю екран зобов'язаний показувати відсутністю рядка.
   */
  it('порожнє значення прибирає ПАРУ цілком — разом із підписом', () => {
    const container = show(
      <KeyValue
        items={[
          { label: 'Started by', value: 'D. Akhmetova' },
          { label: 'Finished', value: '' },
          { label: 'Attempts', value: '3' },
        ]}
      />,
    );

    expect(container.querySelectorAll('dt')).toHaveLength(2);
    expect(screen.queryByText('Finished')).toBeNull();

    // ⛔ І жодного прочерку замість нього.
    expect(container.textContent).not.toContain('—');
    expect(container.textContent).not.toContain('-');
  });

  it.each([null, undefined, '   '])('порожнеча виду %o прибирає пару', (value) => {
    const container = show(
      <KeyValue items={[{ label: 'Finished', value: value as ReactNode }]} />,
    );

    expect(container.querySelector('dl')).toBeNull();
  });

  it('нуль — це дані: пара лишається', () => {
    show(<KeyValue items={[{ label: 'Issues', value: 0 }]} />);

    expect(screen.getByText('Issues')).toBeDefined();
    expect(screen.getByText('0')).toBeDefined();
  });

  it('порожні ВСІ пари — немає й списку: порожній dl теж оголошується', () => {
    const container = show(
      <KeyValue
        items={[
          { label: 'Finished', value: '' },
          { label: 'Error', value: null },
        ]}
      />,
    );

    expect(container.querySelector('dl')).toBeNull();
    expect(container.textContent).toBe('');
  });

  it('порожній перелік — так само нічого', () => {
    const container = show(<KeyValue items={[]} />);

    expect(container.textContent).toBe('');
  });

  it('підказка без значення не лишається сиротою', () => {
    const container = show(
      <KeyValue items={[{ label: 'Finished', value: '', hint: 'у UTC' }]} />,
    );

    expect(container.querySelector('[data-key-value-hint]')).toBeNull();
    expect(screen.queryByText('у UTC')).toBeNull();
  });

  it('підказка ЗІ значенням показується — перевірка не ловить усе підряд', () => {
    show(<KeyValue items={[{ label: 'Started', value: '14:01', hint: 'у UTC' }]} />);

    expect(screen.getByText('у UTC')).toBeDefined();
  });
});
