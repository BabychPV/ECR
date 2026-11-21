import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { TruncatedListNote } from '../TruncatedListNote';
import { TruncatedOptionValue, truncatedOption } from '../truncatedOption';

/**
 * PSS-19c / рецензія L10, L4: перелік, який `fetchAllPages` дочитав лише до запобіжника, каже про це — приміткою під
 * переліком або неактивним останнім пунктом вибору. Повний перелік — без жодного сліду.
 */

const full = { items: [1, 2, 3], nextCursor: null };
const cut = { items: Array.from({ length: 10_000 }, (_, i) => i), nextCursor: 'more' };

describe('TruncatedListNote / truncatedOption', () => {
  it('повний перелік — ні примітки, ні пункту', () => {
    render(
      <MantineProvider>
        <TruncatedListNote list={full} />
        <TruncatedListNote list={undefined} />
      </MantineProvider>,
    );

    expect(screen.queryByRole('note')).toBeNull();
    expect(truncatedOption(full)).toEqual([]);
    expect(truncatedOption(undefined)).toEqual([]);
  });

  it('обрізаний перелік — «показано перші N» (common.shownSoFar), пункт вибору неактивний', () => {
    render(
      <MantineProvider>
        <TruncatedListNote list={cut} />
      </MantineProvider>,
    );

    expect(screen.getByRole('note').textContent).toContain('common.shownSoFar');
    const [option] = truncatedOption(cut);
    expect(option?.value).toBe(TruncatedOptionValue);
    expect(option?.disabled).toBe(true);
    expect(option?.label).toContain('common.shownSoFar');
  });
});
