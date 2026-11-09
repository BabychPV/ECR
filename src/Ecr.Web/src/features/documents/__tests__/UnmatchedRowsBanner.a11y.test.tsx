import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import { issue, table } from '@/features/calculationIssues/__tests__/fixtures';
import { RowLinksPerTable, UnmatchedRowsBanner } from '@/features/documents/UnmatchedRowsBanner';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * Банер «рядки без правила» (UAT 08.11 b1–b4, fg6) в обох темах: рядок підсумку, перемикач з `aria-expanded`,
 * підказка, список груп із кнопками рядків і «і ще N», прокрутна область списку - без блокуючих порушень axe.
 */
describe('UnmatchedRowsBanner — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: розгорнутий банер з групами й посиланнями на рядки без порушень`, async () => {
      const many = Array.from({ length: RowLinksPerTable + 3 }, (_, i) => issue(1, i + 1));
      render(
        <Shell colorScheme={colorScheme}>
          <UnmatchedRowsBanner
            userId={undefined}
            rows={{ issues: [...many, issue(2, 1), issue(7, 1)], hints: [], finishedAt: '2026-11-08T10:00:00Z' }}
            tables={[table(1, '9.01 Unit 400'), table(2, '9.03 Unit 430')]}
            onOpenRow={() => {}}
          />
        </Shell>,
      );

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
