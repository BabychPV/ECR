import { describe, expect, it } from 'vitest';
import { EcrApiError, type EcrProblem } from '@/api/client';
import type { PendingEdit } from '../useCellPatch';
import { cellsOfSaveError } from '../saveErrors';

/**
 * Finding 2 (High, Stage 1): реальна причина відмови збереження
 * («Колонка «C1» очікує число.», ECR-CELL-0422) доходила до клієнта коректно,
 * але ніде не показувалась — ні банером, ні на самій комірці. `cellsOfSaveError`
 * — та частина фіксу, що визначає, ЯКУ комірку підсвітити маркером
 * `.ecr-cell-save-error` (взірець `.ecr-cell-required-input-blocked`, Q-306).
 *
 * ⛔ Дві форми відповіді сервера — обидві РЕАЛЬНІ, жодна не вигадана:
 *  1. Пакетна валідація (`PatchCellsHandler.EnsureValidationPasses`) кладе
 *     готовий `extensions2.cells: [{rowKey, columnCode, ruleCode, message}]`.
 *  2. Відмова читання значення (`CellValueReader.Mismatch`, саме вона стоїть
 *     за «Колонка «C1» очікує число.») несе лише `extensions2.columnCode` —
 *     рядок береться з комірок, які клієнт САМ щойно намагався записати.
 */

function problem(overrides: Partial<EcrProblem> = {}): EcrProblem {
  return {
    title: 'Відмова',
    status: 422,
    errorCode: 'ECR-CELL-0422',
    correlationId: 'c1',
    ...overrides,
  };
}

function edit(rowKey: string, columnCode: string): PendingEdit {
  return { rowKey, columnCode, value: 'abc', isEmpty: false, baseVersion: 'v1' };
}

describe('cellsOfSaveError', () => {
  it('форма 1: сервер уже назвав перелік комірок ({cells: [...]}) — повертається як є', () => {
    const error = new EcrApiError(
      problem({
        detail: 'Валідація відхилила запис: комірок із помилкою — 1.',
        extensions2: {
          cells: [{ rowKey: 'R1', columnCode: 'C1', ruleCode: 'ECR-CELL-0422', message: 'Колонка «C1» очікує число.' }],
        },
      }),
    );

    expect(cellsOfSaveError(error, [])).toEqual([
      { rowKey: 'R1', columnCode: 'C1', ruleCode: 'ECR-CELL-0422', message: 'Колонка «C1» очікує число.' },
    ]);
  });

  it('форма 2: лише columnCode — рядок береться з комірок, які клієнт САМ намагався записати', () => {
    const error = new EcrApiError(
      problem({
        detail: 'Колонка «C1» очікує число.',
        extensions2: { columnCode: 'C1', expected: 'число', actualKind: 'String' },
      }),
    );

    const attempted = [edit('R1', 'C1'), edit('R2', 'C2')];

    // ⛔ Саме репро Stage 1: тільки комірка з ЦІЄЮ колонкою серед фактично
    // надісланих у ЦЬОМУ патчі позначається — не вся таблиця і не колонка,
    // якої в цьому патчі взагалі не було.
    expect(cellsOfSaveError(error, attempted)).toEqual([
      { rowKey: 'R1', columnCode: 'C1', ruleCode: 'ECR-CELL-0422', message: 'Колонка «C1» очікує число.' },
    ]);
  });

  it('форма 2 з кількома рядками тієї самої колонки в одному патчі — позначаються всі', () => {
    const error = new EcrApiError(
      problem({ detail: 'Колонка «C1» очікує число.', extensions2: { columnCode: 'C1' } }),
    );

    const attempted = [edit('R1', 'C1'), edit('R2', 'C1'), edit('R3', 'C2')];

    expect(cellsOfSaveError(error, attempted).map((c) => c.rowKey)).toEqual(['R1', 'R2']);
  });

  it('жодної відомої форми (ні cells, ні columnCode) — порожній список, а не здогад', () => {
    const error = new EcrApiError(
      problem({ errorCode: 'ECR-CELL-0409', detail: 'Конфлікт', extensions2: { conflicts: [] } }),
    );

    expect(cellsOfSaveError(error, [edit('R1', 'C1')])).toEqual([]);
  });

  it('без extensions2 узагалі — порожній список', () => {
    const error = new EcrApiError(problem({ detail: 'Щось пішло не так' }));

    expect(cellsOfSaveError(error, [edit('R1', 'C1')])).toEqual([]);
  });
});
