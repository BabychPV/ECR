import { describe, it, expect } from 'vitest';
import { checkCron, MaxCronLength } from '@/features/integration/cronFormat';

/**
 * Перевірка cron на клієнті проти ФАКТИЧНОЇ поведінки сервера.
 *
 * ⛔ Обидва списки нижче — не вигадані, а виміряні: кожен вираз прогнано
 * через `Quartz.CronExpression.ValidateExpression` 3.13.1 (версія з
 * `Directory.Packages.props`), яку кличе `QuartzJobScheduler.IsValidCron`.
 * «Відхиляє Quartz» — головна половина: пропустити будь-який із них означало б
 * форму, що обіцяє збереження, якого сервер не зробить.
 */

/** Quartz ВІДХИЛЯЄ — отже й форма мусить. */
const RejectedByQuartz = [
  '0 15 2 * * *',
  '0 15 2 ? * ?',
  '0 15 2 * *',
  '60 15 2 * * ?',
  '0 60 2 * * ?',
  '0 0 24 * * ?',
  '0 0 0 0 * ?',
  '0 0 0 32 * ?',
  '0 0 0 * 0 ?',
  '0 0 0 * 13 ?',
  '0 0 0 ? * 0',
  '0 0 0 ? * 8',
  '0 0 0 ? * 6#6',
  '0 0 0 ? * 6#0',
  '0 0 0 32W * ?',
  '0 0 0 L-31 * ?',
  '0 */60 * * * ?',
  '0 0 */24 * * ?',
  '0 0 0 */32 * ?',
  '0 0 0 ? */13 *',
  '0 0 0 ? * */8',
  '0 0 a * * ?',
  '0 0 0 LW-2 * ?',
  '? 0 0 * * ?',
  '0 0 0 * * ? ?',
  '0 0 0 W * ?',
  '0 0 0 L,15 * ?',
  '0 0 0 ? * 1,6L',
  '0 0 0 ? * 2#1,3#1',
  '0 0 0 * JAN-12 ?',
  '0 0 0 ? * 1-FRI',
  '0 0 0 ? * MON-2',
  '0 0 0 * * ?/2',
  '0 0 0 ?/2 * *',
  '0 0 0 ? * 6#',
  '0 0 0 ? * #3',
  '0 0 0 ? * L#2',
  '0 0 0 ? * 6L,2',
  '0 0 0 L/2 * ?',
  '0 0 0 * * ? 2099-1970',
];

/** Quartz ПРИЙМАЄ, і форма теж — інакше вона заважала б звичайним розкладам. */
const AcceptedByQuartz = [
  '0 15 2 * * ?',
  '0 15 2 ? * *',
  '0 15 2 * * ? 2030',
  '0 15 2 * * ? 1970-2030',
  '0 15 2 * * ? *',
  '0 0 0 ? * 7',
  '0 0 0 ? * MON-FRI',
  '0 0 0 ? * mon-fri',
  '0 0 0 ? JAN-MAR MON',
  '0 0 0 ? JAN,MAR MON,WED',
  '0 0 0 ? * 6L',
  '0 0 0 ? * L',
  '0 0 0 ? * 6#3',
  '0 0 0 ? * sat#5',
  '0 0 0 ? * MON#2',
  '0 0 0 ? * FRIL',
  '0 0 0 L * ?',
  '0 0 0 L-3 * ?',
  '0 0 0 L-30 * ?',
  '0 0 0 LW * ?',
  '0 0 0 15W * ?',
  '0/5 * * * * ?',
  '*/5 * * * * ?',
  '0 */59 * * * ?',
  '0 0 */23 * * ?',
  '0 0 0 */31 * ?',
  '0 0 0 ? */12 *',
  '0 0 0 ? * */7',
  '0 0 22-2 * * ?',
  '0 0 1-5/2 * * ?',
  '5-3/2 * * * * ?',
  '0  0  2 * * ?',
  '0 0 0 ? * SUN-SAT',
  '0 0 0 ? * 7-1',
  '0 0 0 * 12-1 ?',
  '0 0 0 31-1 * ?',
  '0 0 * * * ? 2030/2',
  '0 0 0 * * ? 2030,2031',
  '0 0 0 * FEB ?',
];

/**
 * Quartz ПРИЙМАЄ, а форма — ні, і це свідомо (`cronFormat.ts`): незвірені або
 * виродні форми. Список тут, щоб суворість була видимим рішенням, а не
 * випадковістю, яку хтось «полагодить», не знаючи ціни.
 */
const StricterThanQuartz = [
  '0 0 1, * * ?',
  '0 */0 * * * ?',
  '0 0 0 * FEBR ?',
  '0 0 0 * * ? * extra',
  '0 0 0 ? * 6L-7',
  '0 0 0 1-5W * ?',
  '0 0 0 15W,1 * ?',
  '0 15 2 * * ? 2300',
];

describe('checkCron', () => {
  it.each(RejectedByQuartz)('відхиляє те, що відхиляє сервер: %s', (cron) => {
    expect(checkCron(cron)).not.toBeNull();
  });

  it.each(AcceptedByQuartz)('приймає те, що приймає сервер: %s', (cron) => {
    expect(checkCron(cron)).toBeNull();
  });

  it.each(StricterThanQuartz)('суворіша за Quartz (свідомо): %s', (cron) => {
    expect(checkCron(cron)).not.toBeNull();
  });

  it('довжина — перша причина, як і на сервері', () => {
    const long = `0 0 0 ? * ${'1,'.repeat(60)}1`;

    expect(long.length).toBeGreaterThan(MaxCronLength);
    expect(checkCron(long)).toEqual({ key: 'schedule.cronTooLong', params: { max: 100 } });
  });

  it('порожнє й пробіли — окрема причина, не «поле 1»', () => {
    expect(checkCron('   ')).toEqual({ key: 'schedule.cronEmpty' });
  });

  it('причина називає поле і значення так, як їх набрала людина', () => {
    expect(checkCron('0 0 0 ? * mon-2')).toEqual({
      key: 'schedule.cronField',
      params: { position: 6, value: 'mon-2' },
    });
    expect(checkCron('0 0 2 * *')).toEqual({ key: 'schedule.cronFieldCount', params: { count: 5 } });
    expect(checkCron('0 0 2 * * *')).toEqual({ key: 'schedule.cronDayQuestion' });
  });
});
