import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { DocumentTableDto } from '@/api/types';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import * as runtimeApi from '../../api/bindingsRuntimeApi';
import { PermitRefreshStrip } from '../PermitRefreshStrip';

/**
 * Доступність смуги «З дозволу» над сіткою документа в обох темах (Permits, PM-11): стан із кнопкою «Оновити з
 * дозволу», стан із підсумком і попередженнями (збережені рядки поза дозволом - тон `warning`) і вимкнена кнопка
 * документа лише для читання.
 *
 * ⚠ Контраст axe у jsdom не рахує (`test/a11y.ts`); тут перевіряються імена, ролі, `aria-*` і живі області.
 */
vi.mock('@/features/permits/api/bindingsRuntimeApi', () => ({ refreshPermitBinding: vi.fn() }));
vi.mock('@/features/grid/sliceCache', () => ({ invalidateSlices: vi.fn().mockResolvedValue(undefined) }));

const table = {
  allowsDynamicRows: true,
  maxDynamicRows: 200,
  sheetCode: 'Air',
  sheetDefId: 11,
  sheetNameL10n: { values: { en: 'Air' } },
  sheetOrdinal: 1,
  tableCode: 'Emissions',
  tableDefId: 41,
  tableInstanceId: 900,
  tableNameL10n: { values: { en: 'Emission sources' } },
  tableOrdinal: 1,
  permitBinding: { code: 'PB41_Emissions', mode: 'Rows', permitTypeCode: 'AirPermit' },
} as DocumentTableDto;

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('PermitRefreshStrip - доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: кнопка «Оновити з дозволу»`, async () => {
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitRefreshStrip documentId={55} periodKey={202607} tables={[table]} readOnly={false} />
        </Shell>,
      );
      expect(screen.getByTestId('permit-refresh-strip')).toBeDefined();

      const found = await findViolations(container);
      expect(found, describeViolations(found)).toHaveLength(0);
    });

    it(`${colorScheme}: підсумок із попередженнями й збереженими рядками`, async () => {
      vi.mocked(runtimeApi.refreshPermitBinding).mockResolvedValue({
        added: 2,
        updated: 1,
        removedFromPermit: ['P7', 'P9'],
        warnings: [{ code: 'binding.warning.cellNotFromPermit', text: 'x' }],
      });
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitRefreshStrip documentId={55} periodKey={202607} tables={[table]} readOnly={false} />
        </Shell>,
      );

      fireEvent.click(screen.getByRole('button', { name: /doc\.refresh/ }));
      await screen.findByTestId('permit-refresh-result');

      const found = await findViolations(container);
      expect(found, describeViolations(found)).toHaveLength(0);
    });

    it(`${colorScheme}: документ лише для читання - кнопка вимкнена`, async () => {
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <PermitRefreshStrip documentId={55} periodKey={202607} tables={[table]} readOnly />
        </Shell>,
      );
      expect((screen.getByRole('button', { name: /doc\.refresh/ }) as HTMLButtonElement).disabled).toBe(true);

      const found = await findViolations(container);
      expect(found, describeViolations(found)).toHaveLength(0);
    });
  }
});
