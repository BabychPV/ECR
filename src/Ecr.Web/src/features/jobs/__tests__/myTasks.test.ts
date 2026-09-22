import { describe, expect, it } from 'vitest';
import type { JobSummary } from '@/api/types';
import { ActiveJobStates, activeJobCount, isActiveJob } from '@/features/jobs/myTasks';

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
