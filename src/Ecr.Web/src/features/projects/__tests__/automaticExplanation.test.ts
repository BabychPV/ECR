import { describe, expect, it } from 'vitest';
import { automaticExplanation } from '@/features/projects/CurrentPeriodSetting';

/**
 * ✎ UAT project-settings (людина 08.11: «By date it would be January 2026» у листопаді 2026): автоматичний період -
 * найраніший ВІДКРИТИЙ місяць (`PeriodStateCalculator.SelectCurrentPeriod`, D-77), а не місяць дати. Текст має
 * пояснювати, чому він не збігається з сьогоднішнім.
 * Мутація: прибрати гілку «раніше за сьогодні» (завжди `autoIs`) - перший тест червоний; поміняти місцями
 * `autoIsEarlier`/`autoIsLater` - перші два червоні.
 */

const label = (key: number | null): string => (key === null ? 'none' : String(key));
const November2026 = new Date(2026, 10, 8);

describe('automaticExplanation', () => {
  it('автоматичний січень у листопаді - пояснення «найраніший відкритий, сьогодні листопад»', () => {
    expect(automaticExplanation(202601, 'Monthly', label, November2026)).toBe(
      '⟦projectSettings.autoIsEarlier (period=202601, today=202611)⟧',
    );
  });

  it('автоматичний місяць пізніше за сьогоднішній - «сьогоднішній місяць не відкритий у календарі»', () => {
    expect(automaticExplanation(202701, 'Monthly', label, November2026)).toBe(
      '⟦projectSettings.autoIsLater (period=202701, today=202611)⟧',
    );
  });

  it('збігається з сьогоднішнім - коротке «найраніший відкритий місяць»', () => {
    expect(automaticExplanation(202611, 'Monthly', label, November2026)).toBe('⟦projectSettings.autoIs (period=202611)⟧');
  });

  it('немає відкритого - так і сказано; квартальний календар - без порівняння з місяцем', () => {
    expect(automaticExplanation(null, 'Monthly', label, November2026)).toBe('⟦projectSettings.autoNone⟧');
    expect(automaticExplanation(202601, 'Quarterly', label, November2026)).toBe('⟦projectSettings.autoIs (period=202601)⟧');
  });
});
