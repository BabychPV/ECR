import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { ConditionalFormatPanel } from '@/features/templates/ConditionalFormatPanel';
import { ReorderCell, ReorderableRows } from '@/features/templates/ReorderControls';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { createScanClient, settleQueries, Shell, Themes } from '@/test/__tests__/a11yFixtures';

vi.mock('@/features/templates/conditionalFormatApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/templates/conditionalFormatApi')>()),
  getConditionalFormats: vi.fn(() =>
    Promise.resolve({
      etag: '"V1"',
      rules: [
        {
          columnCode: 'Q',
          operator: 'gt',
          value: '100',
          valueTo: null,
          backgroundHex: '#ff0000',
          foregroundHex: null,
          isBold: false,
        },
      ],
    }),
  ),
}));

/**
 * Конструктор шаблону (`ФВ-2.6`, `ФВ-2.7`): редактор умовного форматування і кнопки перестановки колонок —
 * axe без блокуючих порушень в обох темах (`ФВ-14.16`).
 *
 * ⚠ Діалог умовного форматування живе лише в модалці сторінки версії, а та в наборі маршрутів сканується
 * закритою — тому компоненти скануються окремо, з вимкненими кнопками на межах списку.
 *
 * ⛔ Мутаційний доказ (перевірено руками 2026-09-30): прибрати `label` у поля «Приклад значення» → червоний
 * `critical · label` в обох темах.
 */
afterEach(cleanup);

const Columns = [
  { code: 'Q', label: 'Quantity' },
  { code: 'N', label: 'Note' },
];

describe('Конструктор шаблону — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: умовне форматування й перестановка колонок', async (scheme) => {
    const items = ['Alpha', 'Beta', 'Gamma'];
    const client = createScanClient();
    const { container } = render(
      <Shell colorScheme={scheme} client={client}>
        <ConditionalFormatPanel templateVersionId={7} columns={Columns} canEdit />
        <table>
          <thead>
            <tr>
              <th>Order</th>
              <th>Column</th>
            </tr>
          </thead>
          <tbody>
            <ReorderableRows items={items} enabled onMove={() => undefined}>
              {(item, index, drag) => (
                <tr key={item} {...drag.targetProps(index)}>
                  <td>
                    <ReorderCell
                      index={index}
                      count={items.length}
                      name={item}
                      disabled={false}
                      onMove={() => undefined}
                      drag={drag}
                    />
                  </td>
                  <td>{item}</td>
                </tr>
              )}
            </ReorderableRows>
          </tbody>
        </table>
      </Shell>,
    );

    await settleQueries(client);

    // Скануємо дані, а не завантажувач: правило прочитано й показано.
    expect(container.querySelectorAll('[data-focus-row]')).toHaveLength(1);
    expect(container.querySelectorAll('[data-reorder-handle]')).toHaveLength(3);

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
