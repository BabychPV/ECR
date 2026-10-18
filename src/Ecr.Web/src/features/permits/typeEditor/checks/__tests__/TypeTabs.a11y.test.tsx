import { describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { permitType, table } from '../../dialogs/__tests__/fixtures';
import { TypeVersionsTab } from '../../versions/TypeVersionsTab';
import { TypeChecksTab } from '../TypeChecksTab';

/** Доступність вкладок Checks і Versions конструктора типу в обох темах (Permits, PM-08c). */

const withRules = permitType({
  status: 'Published',
  version: 2,
  draftVersion: 3,
  publishedAt: '2026-10-01T08:00:00Z',
  rules: [{ code: 'Dates', nameL10n: { en: 'End after start' }, expression: 'ROW.End > ROW.Start', severity: 'Block' }],
  tables: [
    table({
      rules: [{ code: 'Months', nameL10n: { en: 'Months add up' }, expression: 'SUM(ROW.M) = ROW.Y', severity: 'Warn' }],
    }),
  ],
});

describe('Вкладки Checks і Versions — доступність', { timeout: 120_000 }, () => {
  for (const scheme of Themes) {
    it(`${scheme}: Checks з рядками і порожній`, async () => {
      for (const type of [withRules, permitType()]) {
        const { container } = render(
          <Shell colorScheme={scheme}>
            <TypeChecksTab type={type} onAddCheck={() => {}} />
          </Shell>,
        );
        const violations = await findViolations(container);
        expect(violations, describeViolations(violations)).toHaveLength(0);
        cleanup();
      }
    });

    it(`${scheme}: Versions з опублікованою і чернеткою`, async () => {
      const { container } = render(
        <Shell colorScheme={scheme}>
          <TypeVersionsTab type={withRules} />
        </Shell>,
      );
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
      cleanup();
    });
  }
});
