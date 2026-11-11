import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { TemplateLayoutControl } from '@/features/templates/TemplateLayoutControl';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { createScanClient, Shell, Themes } from '@/test/__tests__/a11yFixtures';

/**
 * ✎ D-474: «Layout» версії шаблону — список (чернетка) і значок лише для читання (опублікована) — axe без блокуючих
 * порушень в обох темах (`ФВ-14.16`). Сторінка версії сканується в наборі маршрутів без структури, тож елемент —
 * окремо, в обох станах.
 */
afterEach(cleanup);

describe('Розкладка версії шаблону — axe без блокуючих порушень', () => {
  it.each(Themes)('тема %s: список і значок', async (scheme) => {
    const client = createScanClient();
    const { container } = render(
      <Shell colorScheme={scheme} client={client}>
        <TemplateLayoutControl templateVersionId={7} layout="PerMonthCopies" editable />
        <TemplateLayoutControl templateVersionId={8} layout="YearWithMonthColumns" editable={false} />
      </Shell>,
    );

    expect(container.querySelector('[data-template-layout="editable"]')).not.toBeNull();
    expect(container.querySelector('[data-template-layout="readonly"]')).not.toBeNull();

    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
