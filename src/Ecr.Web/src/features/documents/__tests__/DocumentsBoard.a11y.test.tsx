import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import type { DocumentSummary } from '@/api/types';
import { DocumentsBoard } from '@/features/documents/DocumentsBoard';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * Дошка документів: картка з 19 аркушами (дефект людини 21.10) в обох темах — без блокуючих
 * порушень axe, смужка аркушів із підписом, що має `title`. Контраст заливки сегмента —
 * `documentsBoardCardCss.test.ts`; геометрія в межах картки — `e2e/documentsBoardCard.spec.ts`.
 */
const sheetStates: Record<string, string> = {};
for (let n = 1; n <= 19; n += 1) sheetStates[`S${String(n)}`] = 'Draft';

const documents = [
  {
    id: 1,
    projectId: 1,
    businessKey: 'DOC-000001',
    nameL10n: { values: { en: 'Document one' } },
    sheetStates,
    modifiedAt: '2026-10-21T10:00:00Z',
    modifiedByDisplayName: 'Operator',
    errorCount: 0,
    hasLateEdits: false,
  },
] as unknown as DocumentSummary[];

describe('DocumentsBoard — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: картка з 19 аркушами — без блокуючих порушень, підпис із title`, async () => {
      render(
        <Shell colorScheme={colorScheme}>
          <DocumentsBoard
            documents={documents}
            documentHref={(id) => `/documents/${String(id)}`}
            projectCode={() => undefined}
            onQuickLook={() => {}}
          />
        </Shell>,
      );

      const card = await screen.findByText('DOC-000001', { selector: 'a' });
      const label = card.closest('[data-board-card]')?.querySelector('[data-segment-summary]');
      expect(label?.getAttribute('data-segment-summary')).toBe('0/19');
      expect(label?.getAttribute('title')).toBeTruthy();

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
