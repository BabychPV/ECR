import type { JSX } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import * as typesApi from '../../../api/typesApi';
import { draftOf } from '../../draftModel';
import { permitType } from '../../__tests__/fixtures';
import { DeleteTableDialog } from '../DeleteTableDialog';

/**
 * Доступність «Remove table» з кількостями у світлій і темній темах (PM-03b2; `ФВ-14.16`): стани «перевіряємо»,
 * опублікована таблиця з числами (відмова) і таблиця лише з чернетки (примітка). Тексти — справжні, із сіду.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));
vi.mock('../../../api/typesApi', () => ({ getPermitTypeUsage: vi.fn() }));

const usage = vi.mocked(typesApi.getPermitTypeUsage);
const type = permitType({ status: 'Published', version: 2, draftVersion: 3 });
const noop = (): void => {};

function mount(scheme: 'light' | 'dark'): void {
  const node: JSX.Element = <DeleteTableDialog type={type} draft={draftOf(type)} tableCode="Storage" onRemove={noop} onClose={noop} />;
  render(<Shell colorScheme={scheme}>{node}</Shell>);
}

async function expectClean(): Promise<void> {
  const violations = await findViolations(screen.getByRole('dialog'));
  expect(violations, describeViolations(violations)).toHaveLength(0);
}

describe('Remove table з кількостями — доступність', { timeout: 120_000 }, () => {
  for (const scheme of Themes) {
    it(`${scheme}: перевіряємо, опублікована таблиця з числами, таблиця лише з чернетки`, async () => {
      usage.mockReturnValueOnce(new Promise(() => undefined));
      mount(scheme);
      await screen.findByRole('status');
      await expectClean();
      cleanup();

      usage.mockResolvedValueOnce({ instanceCount: 3, tables: [{ code: 'Storage', rowCount: 12 }] });
      mount(scheme);
      await screen.findByText(/Rows in this published table: 12/);
      await expectClean();
      cleanup();

      usage.mockResolvedValueOnce({ instanceCount: 3, tables: [] });
      mount(scheme);
      await screen.findByText(/not published yet/);
      await expectClean();
      cleanup();
    });
  }
});
