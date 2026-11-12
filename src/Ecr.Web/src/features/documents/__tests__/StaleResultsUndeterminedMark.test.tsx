import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { StaleResultsUndeterminedMark } from '@/features/documents/StaleResultsMark';
import { testTheme } from '@/test/render';
import { readSeedEnCatalog } from '@/test/seedSql';

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

  /**
   * ✎ 12.11 (U2, живий прохід W15r): «Freshness unknown» не казав ні що це, ні що робити, і нічого не підказувало, що в
   * бейджа є пояснення. ⛔ Червоне без фіксу: знака підказки немає; тексти в сіді — старі.
   */
  it('U2: бейдж має видимий знак підказки (лише ознака, aria-hidden)', () => {
    render(
      <MantineProvider theme={testTheme}>
        <StaleResultsUndeterminedMark />
      </MantineProvider>,
    );

    const badge = document.querySelector('[data-results-stale-undetermined]');
    const sign = badge?.querySelector('[data-hint-affordance]');
    expect(sign).not.toBeNull();
    expect(sign?.getAttribute('aria-hidden')).toBe('true');
  });

  it('U2: у сіді підпис каже, що статус сховано, а підказка — чому і що робити', () => {
    const catalog = readSeedEnCatalog();
    const label = /\(N'documents\.staleUndetermined',\s*N'en',\s*N'((?:[^']|'')*)'/.exec(catalog)?.[1] ?? '';
    const hint = /\(N'documents\.staleUndeterminedHint',\s*N'en',\s*N'((?:[^']|'')*)'/.exec(catalog)?.[1] ?? '';

    expect(label).not.toBe('Freshness unknown');
    expect(label.toLowerCase()).toContain('hidden');
    // Чому: частину документа сховано; що робити: той, хто бачить увесь документ, перевіряє й перераховує.
    expect(hint.toLowerCase()).toContain('hidden from you');
    expect(hint.toLowerCase()).toContain('whole document');
    expect(hint.toLowerCase()).toContain('recalculate');
  });
});
