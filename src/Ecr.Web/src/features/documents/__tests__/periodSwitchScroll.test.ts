import { afterEach, describe, expect, it } from 'vitest';
import {
  clearScrollBeforePeriodSwitch,
  peekScrollBeforePeriodSwitch,
  rememberScrollBeforePeriodSwitch,
} from '../periodSwitchScroll';

afterEach(clearScrollBeforePeriodSwitch);

describe('periodSwitchScroll', () => {
  it('віддає позицію, запам’ятану до перемикання, лише своєму документу', () => {
    rememberScrollBeforePeriodSwitch(1, 120, 0);
    expect(peekScrollBeforePeriodSwitch(1, 10)).toBe(120);
    expect(peekScrollBeforePeriodSwitch(2, 10)).toBeNull();
  });

  it('не перезаписує ще не використаний запис (редирект після скелета бачив би вже 0)', () => {
    rememberScrollBeforePeriodSwitch(1, 120, 0);
    rememberScrollBeforePeriodSwitch(1, 0, 100);
    expect(peekScrollBeforePeriodSwitch(1, 200)).toBe(120);
  });

  it('застарілий запис не діє, а після споживання наступне перемикання пише знову', () => {
    rememberScrollBeforePeriodSwitch(1, 120, 0);
    expect(peekScrollBeforePeriodSwitch(1, 60_000)).toBeNull();
    clearScrollBeforePeriodSwitch();
    rememberScrollBeforePeriodSwitch(1, 40, 1_000);
    expect(peekScrollBeforePeriodSwitch(1, 1_100)).toBe(40);
  });
});
