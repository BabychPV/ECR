import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { PermitDefTable } from '../PermitDefTable';
import { dischargeColumns, dischargeRows } from '../../__tests__/dischargeFixture';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';

/**
 * Доступність широкої таблиці перміта в обох темах (`ФВ-14.16`, `D-127`).
 *
 * Скануються стани: згорнута (за замовчуванням), повністю розгорнута, з підсумками, попередженням і
 * клікабельними рядками. Підписи — з СПРАВЖНЬОГО каталогу `09-seed.sql` (`Catalog`), тож тест
 * заодно доводить, що кожен ключ таблиці існує в сіді (інакше в розмітці стояв би `⟦ключ⟧`).
 */
// ⛔ `Catalog` читається в МОМЕНТ ВИКЛИКУ `t`, а не імпортується всередині фабрики: `a11yFixtures`
// сам тягне `@/shared/i18n`, і `await import(...)` у фабриці мока чекав би на модуль, який чекає
// на цей мок, — прогін висів би без помилки.
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

afterEach(cleanup);

describe('PermitDefTable — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: згорнута, розгорнута, з підсумками й попередженням — без блокуючих порушень`, async () => {
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitDefTable
            columns={dischargeColumns}
            rows={dischargeRows}
            caption="Discharge volumes"
            totals={{ label: 'Σ all 2 rows', sums: { Annual: 192.312 } }}
            warnCell={(row, column) => (row.id === 'd1' && column.code === 'Annual' ? 'Months do not add up' : null)}
            onRowClick={() => undefined}
          />
        </Shell>,
      );

      // Жодного ключа без тексту в каталозі.
      expect(container.textContent).not.toContain('⟦');
      let violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);

      fireEvent.click(screen.getByLabelText(Catalog['permits.table.allColumns'] ?? ''));
      expect(container.textContent).not.toContain('⟦');
      violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
