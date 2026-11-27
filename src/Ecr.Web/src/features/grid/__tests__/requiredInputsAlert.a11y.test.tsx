import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import type { RequiredInputCell } from '@/api/client';
import { mantineProviderProps } from '@/shared/theme/provider';
import { Catalog, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { RequiredInputsBlockedAlert } from '../requiredInputsAlert';
import { NeedsInputCounter } from '../NeedsInputCounter';
import type { NeedsInputCell } from '../needsInput';

/**
 * ✎ 25.11: блок «обов'язкові колонки методології» — axe без блокуючих порушень у обох темах: згорнута кнопка (Warn)
 * з `aria-expanded`, розгорнутий перелік у спливаючому вікні, банер відмови (Block). Тексти — каталог із сіду.
 * ⚠ jsdom без розкладки: контраст реальних кольорів — e2e `requiredInputsBanner.spec.ts` (axe на стенді).
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

afterEach(() => {
  cleanup();
});

const Columns = [
  { code: 'KIND', header: 'Type of fuel' },
  { code: 'A', header: 'Power' },
];
const Rows = [{ rowKey: 'r1', label: null }];
const Needs: NeedsInputCell[] = [
  { rowKey: 'r1', columnCode: 'KIND', level: 'block', source: 'template', methodologies: [], rowIndex: 0, columnIndex: 0 },
  { rowKey: 'r1', columnCode: 'A', level: 'warn', source: 'methodology', methodologies: ['M1'], rowIndex: 0, columnIndex: 1 },
];
const Cells: RequiredInputCell[] = [
  { rowKey: 'r1', columnCode: 'KIND', ruleCode: 'ECR-CALC-0437', message: 'm', methodologies: ['M1', 'M2'] },
  { rowKey: 'r1', columnCode: 'A', ruleCode: 'ECR-CALC-0437', message: 'm', methodologies: ['M1'] },
];

describe('RequiredInputs: a11y', () => {
  it.each(Themes)('%s: DOCUX L2 — лічильник комірок ‹ › і розгорнутий перелік-посилання — без порушень', async (scheme) => {
    const { container } = render(
      <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
        <main>
          <NeedsInputCounter
            cells={Needs}
            tableDefId={1}
            tableInstanceId={1}
            periodKey={202609}
            columnHeader={(code) => Columns.find((column) => column.code === code)?.header ?? code}
            rowLabel={() => 'Row 1'}
          />
        </main>
      </MantineProvider>,
    );

    expect(document.body.textContent).not.toContain('⟦');
    const toggle = screen.getByRole('button', { name: /2 required cell\(s\) empty/ });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(screen.getByRole('button', { name: /Next required cell \(F8\)/ }).getAttribute('aria-keyshortcuts')).toBe('F8');
    expect(screen.getByRole('button', { name: /Previous required cell/ }).getAttribute('aria-keyshortcuts')).toBe('Shift+F8');
    let violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);

    await userEvent.setup().click(toggle);
    await screen.findByRole('list');
    expect(screen.getAllByRole('button', { name: /Type of fuel · Row 1|Power · Row 1/ })).toHaveLength(2);
    expect(document.body.textContent).not.toContain('⟦');
    violations = await findViolations(document.body);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });

  it.each(Themes)('%s: банер відмови — без порушень', async (scheme) => {
    const { container } = render(
      <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
        <main>
          <RequiredInputsBlockedAlert cells={Cells} columns={Columns} rows={Rows} />
        </main>
      </MantineProvider>,
    );

    expect(container.textContent).not.toContain('⟦');
    const violations = await findViolations(container);
    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
