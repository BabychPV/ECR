import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';
import { ReopenedMonthsChip } from '../ReopenedMonthsChip';
import { rangeLabel, reopenedRanges } from '../reopenedRanges';

/**
 * ⛔ AC-1 (b14 doc-notices-compact, людина 25.11: doc 96 після перевідкриття січ–вер — список із 9 рядків «<місяць> is
 * reopened … until Nov 25, 11:59 PM» над сіткою): один чип у шапці з діапазонами. Суміжні місяці з однаковим строком —
 * один діапазон; різні строки чи розрив — окремі; повний перелік — у підказці/описі.
 * Без фіксу червоні: модуля діапазонів і чипа немає (на сторінці — банер-список `document-year-reopened`).
 */
const Y = 2099;
const until = (value: string): string => value.slice(0, 10);

describe('reopenedRanges', () => {
  it('AC-1: січ–вер з однаковим строком — ОДИН діапазон', () => {
    const periods = Array.from({ length: 9 }, (_, i) => ({ periodKey: Y * 100 + i + 1, openUntil: '2099-11-25T21:59:00Z' }));

    const ranges = reopenedRanges(periods, until);
    expect(ranges).toHaveLength(1);
    expect(ranges[0]).toMatchObject({ from: Y * 100 + 1, to: Y * 100 + 9, until: '2099-11-25' });
  });

  it('AC-1: різні строки — окремі діапазони; розрив — теж; порядок — за місяцем', () => {
    const ranges = reopenedRanges(
      [
        { periodKey: Y * 100 + 7, openUntil: '2099-12-01T00:00:00Z' },
        { periodKey: Y * 100 + 1, openUntil: '2099-11-25T00:00:00Z' },
        { periodKey: Y * 100 + 2, openUntil: '2099-11-25T00:00:00Z' },
        { periodKey: Y * 100 + 3, openUntil: '2099-12-01T00:00:00Z' },
        { periodKey: Y * 100 + 8, openUntil: '2099-12-01T00:00:00Z' },
      ],
      until,
    );

    expect(ranges.map((range) => [range.from % 100, range.to % 100, range.until])).toEqual([
      [1, 2, '2099-11-25'],
      [3, 3, '2099-12-01'],
      [7, 8, '2099-12-01'],
    ]);
  });

  it('грудень → січень наступного року — суміжні', () => {
    const ranges = reopenedRanges(
      [
        { periodKey: 209812, openUntil: '2099-01-10T00:00:00Z' },
        { periodKey: 209901, openUntil: '2099-01-10T00:00:00Z' },
      ],
      until,
    );

    expect(ranges).toHaveLength(1);
    expect(rangeLabel(209812, 209901)).toMatch(/2098–.+ 2099$/);
  });
});

describe('ReopenedMonthsChip', () => {
  it('AC-1: один чип із діапазонами; перелік усіх місяців — в описі (підказка/читалка); окремого списку немає', () => {
    const periods = Array.from({ length: 9 }, (_, i) => ({ periodKey: Y * 100 + i + 1, openUntil: '2099-11-25T21:59:00Z' }));
    render(
      <MantineProvider theme={testTheme}>
        <ReopenedMonthsChip periods={periods} timeZoneId="UTC" />
      </MantineProvider>,
    );

    const chip = screen.getByTestId('document-reopened-chip');
    expect(chip.getAttribute('data-reopened-ranges')).toBe('1');
    expect(chip.textContent).toContain('document.notices.reopened');
    expect(chip.textContent).toContain('document.notices.reopenedRange');
    const list = document.getElementById(chip.getAttribute('aria-describedby') ?? '');
    expect(list?.querySelectorAll('li')).toHaveLength(9);
    expect(screen.queryByTestId('document-year-reopened')).toBeNull();
  });

  it('порожній перелік — чипа немає', () => {
    render(
      <MantineProvider theme={testTheme}>
        <ReopenedMonthsChip periods={[]} timeZoneId="UTC" />
      </MantineProvider>,
    );

    expect(screen.queryByTestId('document-reopened-chip')).toBeNull();
  });
});
