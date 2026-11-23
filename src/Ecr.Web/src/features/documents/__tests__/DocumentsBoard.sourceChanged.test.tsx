import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import type { DocumentSummary } from '@/api/types';
import { DocumentsBoard } from '@/features/documents/DocumentsBoard';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * ✎ F5-R (D-570): картка дошки документів із затвердженим місяцем, чиє джерело змінилося після затвердження
 * (`sourceChangedPeriods`), несе ту саму позначку, що й рядок таблиці, — без блокуючих порушень axe в обох темах; картка
 * без поля — без позначки.
 *
 * Мутація: прибрати `SourceChangedMark` із картки `DocumentsBoard` — червоне.
 */
const card = (id: number, sourceChangedPeriods: number[] | null) => ({
  id,
  projectId: 1,
  businessKey: `DOC-00000${String(id)}`,
  nameL10n: { values: { en: `Document ${String(id)}` } },
  sheetStates: { S1: 'Approved' },
  modifiedAt: '2026-10-21T10:00:00Z',
  modifiedByDisplayName: 'Operator',
  errorCount: 0,
  hasLateEdits: false,
  sourceChangedPeriods,
});

const documents = [card(1, [202603, 202604]), card(2, null)] as unknown as DocumentSummary[];

describe('DocumentsBoard — джерело змінено після затвердження', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: позначка на картці з полем, без порушень; без поля — немає`, async () => {
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <DocumentsBoard
            documents={documents}
            documentHref={(id) => `/documents/${String(id)}`}
            projectCode={() => undefined}
            onQuickLook={() => {}}
          />
        </Shell>,
      );

      const marks = container.querySelectorAll('[data-source-changed]');
      expect(marks).toHaveLength(1);
      expect(marks[0]!.getAttribute('data-periods')).toBe('202603,202604');
      expect(marks[0]!.textContent).toContain('documents.sourceChanged');

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
