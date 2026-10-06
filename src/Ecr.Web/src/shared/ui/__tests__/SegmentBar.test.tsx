import type { JSX } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { theme } from '@/shared/theme/theme';
import { SegmentBar } from '@/shared/ui/SegmentBar';

/**
 * `SegmentBar` (`UI-15`/`UI-19`, `KIT.md` §6.7): смужка станів аркушів.
 *
 * ⚠ Каталог тут не завантажено, тож підписи — позначені ключі `⟦…⟧` (`D-138`):
 * саме вони доводять, що підпис іде через каталог, а не через код стану.
 */
afterEach(() => {
  cleanup();
});

function bar(node: JSX.Element): HTMLElement {
  const { container } = render(<MantineProvider theme={theme}>{node}</MantineProvider>);
  const root = container.querySelector<HTMLElement>('[data-segment-bar]');
  if (root === null) throw new Error('SegmentBar не змонтувався');

  return root;
}

const sheets = [
  { code: 'GEN', label: 'General info', state: 'Approved' },
  { code: 'AIR', label: 'Air emissions', state: 'Submitted' },
  { code: 'WTR', label: 'Water', state: 'Draft' },
  { code: 'WST', label: 'Waste', state: 'Rejected' },
];

describe('SegmentBar', () => {
  it('один сегмент на аркуш, у порядку аркушів, зі станом і тоном набору', () => {
    const root = bar(<SegmentBar segments={sheets} />);
    const segments = [...root.querySelectorAll('[data-segment]')];

    expect(segments.map((node) => node.getAttribute('data-segment'))).toEqual(['GEN', 'AIR', 'WTR', 'WST']);
    expect(segments.map((node) => node.getAttribute('data-tone'))).toEqual(['neutral', 'info', 'neutral', 'danger']);
  });

  it('не лише колір: підказка «назва — стан» і повний перелік для читалки', () => {
    const root = bar(<SegmentBar segments={sheets} />);

    expect(root.querySelector('[data-segment="WST"]')?.getAttribute('title')).toBe('Waste — ⟦status.sheet.Rejected⟧');
    const img = root.querySelector('[role="img"]');
    expect(img?.getAttribute('aria-label')).toBe(
      'General info: ⟦status.sheet.Approved⟧, Air emissions: ⟦status.sheet.Submitted⟧, Water: ⟦status.sheet.Draft⟧, Waste: ⟦status.sheet.Rejected⟧',
    );
  });

  it('«N of M approved» рахує затверджені з ПЕРЕДАНИХ сегментів', () => {
    const root = bar(<SegmentBar segments={sheets} />);

    // ⛔ Мутаційний доказ: рахувати `Submitted` як готовий — тут «2/4».
    expect(root.querySelector('[data-segment-summary]')?.getAttribute('data-segment-summary')).toBe('1/4');
  });

  it('невідомий стан — тон «увага», а не нейтральний', () => {
    const root = bar(<SegmentBar segments={[{ code: 'X', label: 'X', state: 'Returned' }]} />);

    expect(root.querySelector('[data-segment="X"]')?.getAttribute('data-tone')).toBe('warning');
  });

  it('summary={false} — без підпису; size="lg" — позначка розміру', () => {
    const root = bar(<SegmentBar segments={sheets} summary={false} size="lg" />);

    expect(root.querySelector('[data-segment-summary]')).toBeNull();
    expect(root.getAttribute('data-size')).toBe('lg');
  });
});
