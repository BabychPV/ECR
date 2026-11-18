import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { mantineProviderProps } from '@/shared/theme/provider';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Themes } from '@/test/__tests__/a11yFixtures';
import { YearReopenedBanner } from '../YearReopenedBanner';

/** ✎ L7c: банер відкритих для документа місяців року — axe без порушень у обох темах; порожній перелік — нічого. */
const Periods = [
  { periodKey: 209903, openUntil: '2099-12-20T18:00:00Z' },
  { periodKey: 209907, openUntil: '2099-12-21T18:00:00Z' },
];

afterEach(() => {
  cleanup();
});

describe('YearReopenedBanner', () => {
  it.each(Themes)('axe, тема %s', async (scheme) => {
    const { container } = render(
      <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
        <main>
          <h1>Document</h1>
          <YearReopenedBanner periods={Periods} timeZoneId="UTC" />
        </main>
      </MantineProvider>,
    );

    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
    expect(container.querySelectorAll('[data-year-reopened-month]')).toHaveLength(2);
    expect(container.querySelector('[role="status"]')).not.toBeNull();
  });

  it('порожній перелік — банера немає', () => {
    const { container } = render(
      <MantineProvider {...mantineProviderProps}>
        <YearReopenedBanner periods={[]} timeZoneId="UTC" />
      </MantineProvider>,
    );
    expect(container.querySelector('[data-testid="document-year-reopened"]')).toBeNull();
  });
});
