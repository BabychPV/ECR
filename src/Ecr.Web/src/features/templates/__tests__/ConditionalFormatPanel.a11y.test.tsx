import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { ConditionalFormatPanel } from '@/features/templates/ConditionalFormatPanel';
import { ReorderCell, ReorderableRows } from '@/features/templates/ReorderControls';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';

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
    const { container } = render(
      <Shell colorScheme={scheme}>
        <ConditionalFormatPanel columns={Columns} />
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

    expect(container.querySelectorAll('[data-reorder-handle]')).toHaveLength(3);

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
