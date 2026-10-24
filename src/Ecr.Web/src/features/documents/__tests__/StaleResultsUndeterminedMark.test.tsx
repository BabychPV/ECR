import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { StaleResultsUndeterminedMark } from '@/features/documents/StaleResultsMark';
import { testTheme } from '@/test/render';

/**
 * ✎ D-446: позначка «актуальність результатів не визначається» (`resultsStaleUndetermined === 'PartiallyHidden'`)
 * пояснює себе з клавіатури й для читалки — як `LateEditsMark`: Tab доходить до бейджа, фокус відкриває
 * `role="tooltip"` з поясненням, і те саме пояснення — опис бейджа.
 *
 * ⚠ Рядки — ключами в `⟦…⟧`: каталогу в цьому тесті немає.
 */
describe('StaleResultsUndeterminedMark: пояснення доступне з фокуса', () => {
  it('Tab доводить до бейджа, фокус відкриває підказку, опис — те саме пояснення', async () => {
    render(
      <MantineProvider theme={testTheme}>
        <button type="button">before</button>
        <StaleResultsUndeterminedMark />
      </MantineProvider>,
    );

    const hint = '⟦documents.staleUndeterminedHint⟧';
    const badge = screen.getByText('⟦documents.staleUndetermined⟧').closest('[data-results-stale-undetermined]');
    expect(badge).not.toBeNull();
    expect(badge?.getAttribute('data-results-stale-undetermined')).toBe('PartiallyHidden');
    // ⛔ Це не «застаріло»: ні атрибута, ні слова позначки застарілості.
    expect(document.querySelector('[data-results-stale]')).toBeNull();
    expect(screen.queryByText('⟦documents.methodologyResultsStale⟧')).toBeNull();

    const user = userEvent.setup();
    await user.tab();
    await user.tab();
    expect(document.activeElement).toBe(badge);

    expect((await screen.findByRole('tooltip')).textContent).toBe(hint);
    expect(screen.getAllByRole('generic', { description: hint })).toContain(badge);
  });
});
