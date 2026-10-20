import axe from 'axe-core';
import { describe, expect, it } from 'vitest';
import type { PluginProviders } from '@revolist/revogrid';
import { GridAriaPlugin } from '../gridAria';

/**
 * P2-1 (прохід B): axe `aria-required-children` (critical) на `revo-grid` документа.
 * Фокусована `revogr-viewport-scroll` без ролі — «недозволена дитина» `role=grid`. Розмітка нижче повторює
 * реальну структуру RevoGrid 4.11 (ролі рядків/комірок виставляє вбудований `WCAGPlugin`).
 */
function realisticGrid(): HTMLRevoGridElement {
  const element = document.createElement('revo-grid');
  element.innerHTML =
    '<revogr-attribution><a href="#">RevoGrid</a></revogr-attribution>' +
    '<div class="main-viewport"><div class="viewports">' +
    '<revogr-viewport-scroll class="colPinStart"><div class="inner-content-table">' +
    '<revogr-header><div class="header-rgRow"><div role="columnheader" aria-label="A">A</div></div></revogr-header>' +
    '<revogr-data><div role="row" class="rgRow"><div role="gridcell">1</div></div></revogr-data>' +
    '</div></revogr-viewport-scroll>' +
    '<revogr-viewport-scroll class="rgCol"><div class="inner-content-table">' +
    '<revogr-header><div class="header-rgRow"><div role="columnheader" aria-label="B">B</div></div></revogr-header>' +
    '<revogr-data><div role="row" class="rgRow"><div role="gridcell">2</div></div></revogr-data>' +
    '<revogr-overlay-selection><revogr-edit></revogr-edit></revogr-overlay-selection>' +
    '</div></revogr-viewport-scroll>' +
    '</div></div>';
  document.body.append(element);
  return element as unknown as HTMLRevoGridElement;
}

async function violationsOf(element: Element): Promise<string[]> {
  const results = await axe.run(element, {
    runOnly: { type: 'rule', values: ['aria-required-children', 'aria-required-parent'] },
    resultTypes: ['violations'],
  });
  return results.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${n.failureSummary ?? ''}`));
}

describe('GridAriaPlugin + axe', () => {
  it('фокусована прокручувана область не порушує aria-required-children / aria-required-parent', async () => {
    const element = realisticGrid();
    const plugin = new GridAriaPlugin(element, {} as PluginProviders);
    const scroller = element.querySelector('revogr-viewport-scroll.rgCol');

    // Обидві вимоги разом: область лишається фокусованою (axe `scrollable-region-focusable`, CL-17)…
    expect(scroller?.getAttribute('tabindex')).toBe('0');
    // …а в дереві `grid` вона має дозволену роль.
    expect(await violationsOf(element)).toEqual([]);
    expect(scroller?.getAttribute('role')).toBe('rowgroup');

    plugin.destroy();
    element.remove();
  });
});
