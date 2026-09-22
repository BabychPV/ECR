import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { theme } from '@/shared/theme/theme';
import {
  StatStrip,
  StatStripMaxItems,
  capItems,
  problemTone,
  type StatItem,
  type StatStripItems,
} from '@/shared/ui/StatStrip';

/**
 * `UI-06`, шар 3: смуга показників переліку.
 *
 * Перевіряються рівно ті твердження, які директива №15 §0 записала як
 * правила: `L4` (≤ 4 цифри — і в ТИПІ, і в рантаймі), клац фільтрує й
 * позначається `aria-pressed`, `L3` (колір лише в показника-проблеми),
 * `D15-06` (без даних не малюється).
 */

function stat(id: string, value: number, extra: Partial<StatItem> = {}): StatItem {
  return { id, label: id, value, ...extra };
}

function show(node: JSX.Element): HTMLElement {
  const { container } = render(
    <MantineProvider theme={theme}>
      <div data-probe="">{node}</div>
    </MantineProvider>,
  );

  const probe = container.querySelector<HTMLElement>('[data-probe]');
  if (probe === null) throw new Error('пробний вузол не змонтувався');

  return probe;
}

const three: StatStripItems = [stat('running', 3), stat('queued', 12), stat('failed', 0)];

afterEach(cleanup);

describe('StatStrip — одна приглушена смуга (KIT.md §1.6)', () => {
  it('це ОДНА група з іменем, а не набір плиток', () => {
    const container = show(<StatStrip label="Campaign summary" items={three} />);

    expect(screen.getByRole('group', { name: 'Campaign summary' })).toBeDefined();
    expect(container.querySelectorAll('[data-stat-strip]')).toHaveLength(1);
    expect(container.querySelectorAll('[data-stat]')).toHaveLength(3);
  });

  it('число — моноширинне, підпис — дрібний', () => {
    const container = show(<StatStrip label="Summary" items={[stat('queued', 12)]} />);

    const value = container.querySelector<HTMLElement>('[data-stat-value]');
    const label = container.querySelector<HTMLElement>('[data-stat-label]');

    expect(value?.textContent).toBe('12');
    expect(value?.getAttribute('style') ?? '').toContain('monospace');
    expect(label?.textContent).toBe('queued');
  });

  it('знаменник показується разом зі значенням: 48 / 60', () => {
    const container = show(<StatStrip label="Summary" items={[stat('filled', 48, { of: 60 })]} />);

    expect(container.querySelector('[data-stat-value]')?.textContent).toBe('48 / 60');
  });

  it('число йде через formatNumber — локаллю продукту, а не рядком', () => {
    const container = show(<StatStrip label="Summary" items={[stat('sheets', 12_345)]} />);

    // `12,345`, а не `12345`: інакше жоден роздільник тисяч не перевірений.
    expect(container.querySelector('[data-stat-value]')?.textContent).toBe('12,345');
  });
});

describe('L4: показників не більше чотирьох', () => {
  /*
   * ⛔ Перша половина доказу — ТИПОМ. Директива §0 вимагає саме цього:
   * «тип `StatStripProps.items` — кортеж довжини ≤ 4». Рядок нижче
   * компілюється ТІЛЬКИ тому, що `@ts-expect-error` гасить справжню помилку
   * компілятора; варто розширити `StatStripItems` до `readonly StatItem[]` —
   * і `tsc --noEmit` упаде вже на цьому файлі з «Unused '@ts-expect-error'
   * directive». Тобто перевірка живе в гейті `client`, а не лише тут.
   */
  // @ts-expect-error L4: п'ятий показник не підходить до жодного з чотирьох кортежів
  const five: StatStripItems = [
    stat('a', 1),
    stat('b', 2),
    stat('c', 3),
    stat('d', 4),
    stat('e', 5),
  ];

  it('чотири — це межа, а не дефолт', () => {
    expect(StatStripMaxItems).toBe(4);
  });

  it('рівно чотири проходять і малюються всі', () => {
    const four: StatStripItems = [stat('a', 1), stat('b', 2), stat('c', 3), stat('d', 4)];
    const container = show(<StatStrip label="Summary" items={four} />);

    expect(container.querySelectorAll('[data-stat]')).toHaveLength(4);
  });

  /*
   * ⛔ Друга половина — РАНТАЙМ, для того, що прийшло повз типи (`as`,
   * відповідь сервера). Саме виняток, а не `console.warn` прототипу: тихо
   * відкинутий п'ятий показник доїхав би до користувача.
   */
  it('п’ятий показник у режимі розробки — виняток, а не тихе відкидання', () => {
    expect(() => capItems(five)).toThrowError(/L4/);
    expect(() => capItems(five)).toThrowError(/передано 5/);
  });

  it('виняток називає показники поіменно — інакше шукати нічого', () => {
    expect(() => capItems(five)).toThrowError(/a, b, c, d, e/);
  });

  it('рендер із п’ятьма теж падає, а не малює чотири', () => {
    expect(() => show(<StatStrip label="Summary" items={five} />)).toThrowError(/L4/);
  });
});

describe('Клац по показнику фільтрує перелік', () => {
  function Filterable({ onSelect }: { onSelect: (id: string | null) => void }): JSX.Element {
    const [active, setActive] = useState<string | null>(null);

    return (
      <StatStrip
        label="Campaign summary"
        items={three}
        active={active}
        onSelect={(id) => {
          setActive(id);
          onSelect(id);
        }}
      />
    );
  }

  it('клац викликає onSelect з ідентифікатором і ставить aria-pressed="true"', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();

    show(<Filterable onSelect={onSelect} />);

    const running = screen.getByRole('button', { name: /running/ });

    expect(running.getAttribute('aria-pressed')).toBe('false');

    await user.click(running);

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith('running');
    expect(screen.getByRole('button', { name: /running/ }).getAttribute('aria-pressed')).toBe(
      'true',
    );
  });

  it('повторний клац ЗНІМАЄ фільтр: onSelect(null) і aria-pressed="false"', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();

    show(<Filterable onSelect={onSelect} />);

    await user.click(screen.getByRole('button', { name: /running/ }));
    await user.click(screen.getByRole('button', { name: /running/ }));

    expect(onSelect).toHaveBeenNthCalledWith(2, null);
    expect(screen.getByRole('button', { name: /running/ }).getAttribute('aria-pressed')).toBe(
      'false',
    );
  });

  it('клац по ІНШОМУ показнику переносить позначку, а не додає другу', async () => {
    const user = userEvent.setup();

    show(<Filterable onSelect={() => {}} />);

    await user.click(screen.getByRole('button', { name: /running/ }));
    await user.click(screen.getByRole('button', { name: /queued/ }));

    const pressed = screen
      .getAllByRole('button')
      .filter((button) => button.getAttribute('aria-pressed') === 'true');

    expect(pressed).toHaveLength(1);
    expect(pressed[0]?.getAttribute('data-stat')).toBe('queued');
  });

  it('без onSelect показники — не кнопки: кнопка без дії гірша за текст', () => {
    const container = show(<StatStrip label="Summary" items={three} />);

    expect(screen.queryAllByRole('button')).toHaveLength(0);
    expect(container.querySelectorAll('[aria-pressed]')).toHaveLength(0);
  });

  it('filter: false лишає показник цифрою навіть за наявного onSelect', () => {
    const items: StatStripItems = [stat('changed', 4), stat('breaking', 2, { filter: false })];

    show(<StatStrip label="Summary" items={items} onSelect={() => {}} />);

    expect(screen.getByRole('button', { name: /changed/ })).toBeDefined();
    expect(screen.queryByRole('button', { name: /breaking/ })).toBeNull();
  });

  it('підказка йде через спільний Hint (aria-describedby), а без неї — нічого зайвого', () => {
    const items: StatStripItems = [
      stat('issues', 7, { hint: 'Filter the list: validation errors' }),
      stat('drafts', 3),
    ];

    const container = show(<StatStrip label="Summary" items={items} />);
    const nodes = container.querySelectorAll('[data-stat]');

    // ⛔ Голого `title` більше немає — `Hint` описує показник через
    // `aria-describedby`, що вказує на прихований вузол із тим самим текстом.
    expect(nodes[0]?.hasAttribute('title')).toBe(false);

    const describedBy = nodes[0]?.getAttribute('aria-describedby') ?? null;
    expect(describedBy).toBeTruthy();

    // ⚠ `useId()` дає значення з двокрапками (`:r3c:`) — не валідний CSS-
    // селектор без екранування, тож пошук іде через `getElementById`, не
    // `querySelector('#...')`.
    const description = describedBy === null ? null : container.ownerDocument.getElementById(describedBy);
    expect(description?.textContent).toBe('Filter the list: validation errors');

    expect(nodes[1]?.hasAttribute('aria-describedby')).toBe(false);
    expect(nodes[1]?.hasAttribute('title')).toBe(false);
  });
});

describe('L3: колір — лише в показника-ПРОБЛЕМИ', () => {
  /*
   * ⚠ Перевіряється РОЗМІТКА (`data-stat-tone`), а не обчислений CSS-колір:
   * jsdom не рахує розкладки, `getComputedStyle` над змінною теми віддав би
   * порожній рядок, і тест був би зеленим за будь-якого кольору. Тон —
   * рішення компонента, і саме воно тут і стережеться.
   */
  it('7 validation errors — тон danger', () => {
    const container = show(
      <StatStrip label="Summary" items={[stat('issues', 7, { tone: 'danger' })]} />,
    );

    expect(container.querySelector('[data-stat]')?.getAttribute('data-stat-tone')).toBe('danger');
  });

  it('0 validation errors — «все гаразд», тобто НЕЙТРАЛЬНО', () => {
    const container = show(
      <StatStrip label="Summary" items={[stat('issues', 0, { tone: 'danger' })]} />,
    );

    expect(container.querySelector('[data-stat]')?.getAttribute('data-stat-tone')).toBe('neutral');
  });

  it('показник без тону нейтральний за будь-якого значення', () => {
    const container = show(<StatStrip label="Summary" items={[stat('running', 42)]} />);

    expect(container.querySelector('[data-stat]')?.getAttribute('data-stat-tone')).toBe('neutral');
  });

  it.each<[number, 'danger' | null]>([
    [7, 'danger'],
    [1, 'danger'],
    [0, null],
    [-3, null],
  ])('problemTone(%i) → %s', (value, expected) => {
    expect(problemTone(stat('issues', value, { tone: 'danger' }))).toBe(expected);
  });

  it('колір береться з toneFills StatusBadge, а не власним літералом', () => {
    const container = show(
      <StatStrip label="Summary" items={[stat('issues', 7, { tone: 'danger' })]} />,
    );

    const style = container.querySelector('[data-stat-value]')?.getAttribute('style') ?? '';

    // `toneFills.danger.text` — дослівно цей токен (`StatusBadge.tsx`).
    expect(style).toContain('var(--ecr-danger)');
  });

  it('нейтральний показник бере токен тексту, а не відсутність кольору', () => {
    const container = show(<StatStrip label="Summary" items={[stat('running', 3)]} />);

    const style = container.querySelector('[data-stat-value]')?.getAttribute('style') ?? '';

    expect(style).toContain('var(--ecr-text)');
    expect(style).not.toContain('var(--ecr-danger)');
  });
});

describe('Активність видно не самим лише кольором (ФВ-14.18)', () => {
  it('активний показник напівжирний і підкреслений, крім aria-pressed', () => {
    const container = show(
      <StatStrip label="Summary" items={three} active="queued" onSelect={() => {}} />,
    );

    const active = container.querySelector('[data-stat="queued"] [data-stat-label]');
    const idle = container.querySelector('[data-stat="running"] [data-stat-label]');

    expect(active?.getAttribute('style') ?? '').toContain('underline');
    expect(idle?.getAttribute('style') ?? '').not.toContain('underline');
  });

  it('active із пропа, а не з власного стану: смуга керована', () => {
    show(<StatStrip label="Summary" items={three} active="failed" onSelect={() => {}} />);

    expect(screen.getByRole('button', { name: /failed/ }).getAttribute('aria-pressed')).toBe(
      'true',
    );
  });
});

describe('D15-06: смуги без показників не буває', () => {
  it('порожній перелік (повз типи) — не порожня група, а НІЧОГО', () => {
    const container = show(
      <StatStrip label="Summary" items={[] as unknown as StatStripItems} />,
    );

    expect(container.textContent).toBe('');
    expect(screen.queryByRole('group')).toBeNull();
  });

  it('нуль — це дані: показник із нулем лишається', () => {
    const container = show(<StatStrip label="Summary" items={[stat('failed', 0)]} />);

    expect(container.querySelector('[data-stat-value]')?.textContent).toBe('0');
  });
});
