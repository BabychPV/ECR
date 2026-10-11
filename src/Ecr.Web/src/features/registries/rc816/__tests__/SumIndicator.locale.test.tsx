import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';
import { SumIndicator } from '../SumIndicator';
import type { ChildSumRule } from '../composition';
import type { PendingRow } from '../pendingRows';

/**
 * AN-40b: Σ складу в ru/kz — з комою, як сітка й решта екранів, а не інваріантною крапкою.
 *
 * ⛔ Мутаційний доказ: поверни `sum: state.sum` без `shown()` в `SumIndicator` — у ru лишається `99.8`.
 */
const ui = vi.hoisted(() => ({ language: 'ru' as string }));

vi.mock('@/shared/i18n', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/shared/i18n')>();
  return { ...original, language: () => ui.language };
});

const Rule: ChildSumRule = { code: 'SUM_100', child: 'GAS', field: 'MOL_PCT', target: '100', tolerance: '0.5', severity: 'Error' };

function row(value: string, key: string): PendingRow {
  return {
    key,
    id: 1,
    code: key,
    display: key,
    version: null,
    values: { MOL_PCT: value },
    original: { MOL_PCT: value },
    deleted: false,
  } as unknown as PendingRow;
}

function sumLine(): string {
  return document.querySelector('[data-rc816-sum="SUM_100"]')?.textContent ?? '';
}

afterEach(() => {
  cleanup();
  ui.language = 'ru';
});

describe('SumIndicator: мова подання числа (AN-40b)', () => {
  it('ru: Σ, ціль і допуск — з комою', () => {
    render(
      <MantineProvider theme={testTheme}>
        <SumIndicator rule={Rule} rows={[row('50.3', 'a'), row('49.5', 'b')]} />
      </MantineProvider>,
    );

    expect(sumLine()).toContain('sum=99,8');
    expect(sumLine()).toContain('tolerance=0,5');
    expect(sumLine()).not.toContain('99.8');
  });

  it('en: крапка', () => {
    ui.language = 'en';
    render(
      <MantineProvider theme={testTheme}>
        <SumIndicator rule={Rule} rows={[row('50.3', 'a'), row('49.5', 'b')]} />
      </MantineProvider>,
    );

    expect(sumLine()).toContain('sum=99.8');
  });
});
