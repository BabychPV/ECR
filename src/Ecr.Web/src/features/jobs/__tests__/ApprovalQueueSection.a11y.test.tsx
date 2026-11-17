import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, waitFor } from '@testing-library/react';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { ApprovalQueueSection } from '../ApprovalQueueSection';

/**
 * ✎ B79 (Q13): черга «До погодження» з рядками — одиночний рядок із позначкою «перераховано після подання», рядок без
 * неї і група місяців (розгорнута кнопкою) — в обох темах без блокуючих порушень axe (імена посилань і кнопок,
 * `aria-expanded`/`aria-controls` групи).
 *
 * ⚠ Контраст axe у jsdom НЕ рахує (`src/test/a11y.ts`): позначка — текст токеном `--ecr-warning`
 * (`toneFills.warning`), а його контраст на кожній поверхні в обох темах стереже
 * `shared/theme/__tests__/contrast.test.ts` («статусні кольори: текст … статусного відтінку»).
 *
 * Загальний набір маршрутів черги з рядками не бачив: `a11yFixtures.emptyBodyFor` віддає порожню чергу, а шухляда
 * «My tasks» там закрита. Тому тут власний мок відповіді.
 */
const row = {
  documentId: 11,
  documentKey: 'AIR-2026-09',
  projectCode: 'AIR',
  templateCode: 'AIR',
  sheetDefId: 3,
  sheetCode: 'Stationary',
  periodKey: 202609,
  submittedAt: '2026-10-07T09:30:00Z',
  submittedByDisplayName: 'D. Akhmetova',
  stepOrdinal: 1,
  stepCount: 2,
  stepRoleCode: 'Approver',
  recalculatedAfterSubmission: false,
};

const queue = {
  items: [
    { ...row, recalculatedAfterSubmission: true },
    { ...row, documentId: 12, documentKey: 'WTR-2026-09', sheetDefId: 4, sheetCode: 'Water' },
    { ...row, documentId: 13, documentKey: 'LND-2026', sheetDefId: 5, sheetCode: 'Land', periodKey: 202601 },
    { ...row, documentId: 13, documentKey: 'LND-2026', sheetDefId: 5, sheetCode: 'Land', periodKey: 202602, recalculatedAfterSubmission: true },
  ],
  nextCursor: null,
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Черга «До погодження» з рядками і позначкою — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: рядок із позначкою, рядок без неї і група місяців без порушень`, async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn(() =>
          Promise.resolve(new Response(JSON.stringify(queue), { status: 200, headers: { 'Content-Type': 'application/json' } })),
        ),
      );
      render(
        <Shell colorScheme={colorScheme}>
          <main>
            <ApprovalQueueSection opened />
          </main>
        </Shell>,
      );

      await waitFor(() => expect(document.querySelector('[data-approval-document="11"]')).not.toBeNull());

      // Фікстура справді несе позначку: один рядок поза групою і один усередині групи.
      expect(document.querySelectorAll('[data-recalculated-after-submission]').length).toBe(2);
      expect(document.querySelector('[data-approval-document="12"] [data-recalculated-after-submission]')).toBeNull();
      expect(document.querySelector('[data-approval-group="13:5"]')).not.toBeNull();

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
