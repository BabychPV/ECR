import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { DocumentProgress } from '../DocumentProgress';

/**
 * ✎ B143 (B75): позначка «No approval» у рядку прогресу документа (шаблон без подання й затвердження) в обох
 * темах — без блокуючих порушень axe (контраст нейтральної плашки, підпис словом).
 */
describe('Рядок прогресу документа: позначка «Без погодження» — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: позначка «No approval» без порушень`, async () => {
      render(
        <Shell colorScheme={colorScheme}>
          <main>
            <DocumentProgress
              documentId={96}
              periodKey={202611}
              sheets={[{ code: 'S1', name: 'Emissions', state: 'Draft' }]}
              activeCode="S1"
              issues={null}
              onShowIssues={() => undefined}
              workflowOff
            />
          </main>
        </Shell>,
      );
      await screen.findByTestId('document-no-approval');

      const violations = await findViolations(document.body);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
