import { describe, expect, it } from 'vitest';
import type { JobSummary } from '@/api/types';
import {
  ActiveJobStates,
  activeJobCount,
  isActiveJob,
  isShownInMyTasks,
  myTaskMessage,
} from '@/features/jobs/myTasks';

/**
 * Лічильник позначки «My tasks» (`UI-07`, `UX-09`).
 *
 * ⛔ Мутаційний доказ. Замініть фільтр у `activeJobCount` на `list.length` — і
 * перший же випадок нижче почервоніє: у наборі три задачі, активна одна.
 * Саме ця мутація й описує дефект, заради якого лічильник винесений окремо:
 * позначка «3» над переліком, де все давно завершилось, повідомляє неправду.
 */

function job(state: string): JobSummary {
  return {
    jobCode: 'Ecr.Application.Ports.IExcelExportJob',
    jobId: `IExcelExportJob#${state}`,
    percent: 0,
    startedAt: '2026-09-22T10:00:00Z',
    state,
    updatedAt: '2026-09-22T10:00:00Z',
  } as JobSummary;
}

describe('activeJobCount', () => {
  it('рахує лише Queued/Running, а не довжину переліку', () => {
    const list = [job('Succeeded'), job('Running'), job('Failed')];

    expect(activeJobCount(list)).toBe(1);
    expect(list).toHaveLength(3);
  });

  it('перелік іще не приїхав — нуль, а не блимання позначки', () => {
    expect(activeJobCount(undefined)).toBe(0);
  });

  it.each([
    ['Queued', true],
    ['Running', true],
    ['Succeeded', false],
    ['Failed', false],
    ['Cancelled', false],
    // ⚠ `state` доходить до клієнта простим `string` (`schema.d.ts`), тож
    // невідоме значення має бути НЕактивним: позначка, що рахує стани, яких
    // клієнт не розуміє, ніколи не згасне.
    ['Unavailable', false],
  ])('стан %s: активний = %s', (state, expected) => {
    expect(isActiveJob(state)).toBe(expected);
  });

  it('перелік активних станів — рівно два, як у CancelJobHandler.Active', () => {
    expect([...ActiveJobStates].sort()).toEqual(['Queued', 'Running']);
  });
});

describe('F-27: що і як показує «My tasks»', () => {
  const base = {
    jobId: 'x',
    percent: 100,
    startedAt: '2026-09-22T10:00:00Z',
    updatedAt: '2026-09-22T10:00:00Z',
  };

  it('успішний перерахунок формул не показується, провалений і активний — так', () => {
    const recalc = (state: string): JobSummary =>
      ({ ...base, jobCode: 'Ecr.Application.Ports.IFormulaRecalculationJob', state }) as JobSummary;

    // ⛔ Мутація: `isShownInMyTasks` повертає `true` завжди — перший вираз червоний.
    expect(isShownInMyTasks(recalc('Succeeded'))).toBe(false);
    expect(isShownInMyTasks(recalc('Failed'))).toBe(true);
    expect(isShownInMyTasks(recalc('Running'))).toBe(true);
    expect(isShownInMyTasks(job('Succeeded'))).toBe(true);
  });

  it('ідентифікатор файлу експорту замінено людським текстом, решта повідомлень — як є', () => {
    const exported = {
      ...job('Succeeded'),
      message: '3f1c0b0e9a2d4c6e8b7a5f4d3c2b1a09',
    } as JobSummary;

    // ⛔ Мутація: повертати `job.message` як є — тут hex.
    expect(myTaskMessage(exported)).toBe('⟦jobs.exportReady⟧');
    expect(myTaskMessage({ ...exported, message: 'Exporting sheet 2 of 5' } as JobSummary)).toBe(
      'Exporting sheet 2 of 5',
    );
    expect(myTaskMessage({ ...exported, message: null } as JobSummary)).toBeNull();

    // Той самий hex у НЕ-експорті — не наш випадок, лишається як є.
    expect(
      myTaskMessage({ ...exported, jobCode: 'Ecr.Application.Ports.IExcelImportJob' } as JobSummary),
    ).toBe('3f1c0b0e9a2d4c6e8b7a5f4d3c2b1a09');
  });
});
