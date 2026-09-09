import { describe, it, expect, vi } from 'vitest';
import type { ColumnDto, TableSliceDto } from '@/api/types';
import { cellKey, decide, guardOf } from '@/features/grid/permissions';

/** Права по комірках приходять із сервера і показуються, а не вгадуються. */
function column(overrides: Partial<ColumnDto> = {}): ColumnDto {
  return {
    id: 1,
    code: 'C1',
    header: 'Обсяг',
    dataType: 'Decimal',
    ordinal: 1,
    isReadOnly: false,
    isRequired: false,
    displayFormat: null,
    defaultValue: null,
    lookupRegistryDefId: null,
    unitId: null,
    unitSymbol: null,
    ...overrides,
  };
}

function slice(permissions: Record<string, string>, columns: ColumnDto[] = [column()]): TableSliceDto {
  return {
    tableInstanceId: 1,
    periodKey: 202603,
    columns,
    rows: [
      {
        rowKey: 'R1',
        ordinal: 1,
        rowKind: 'Static',
        label: null,
        rowVersion: '0x01',
        cells: {},
        isOrphaned: false,
      },
    ],
    cellPermissions: permissions,
  };
}

describe('Права по комірках', () => {
  it('ФВ-14.3: read-only комірки візуально відрізняються', () => {
    const decision = decide(slice({}), 'R1', column({ isReadOnly: true }));

    expect(decision.editable).toBe(false);

    // ⚠ Назва причини — ДОСЛІВНО серверна (`EditDenyReason.ColumnReadOnly`).
    // До аудиту клієнт мав власну `ReadOnlyColumn`, і сервер із ним не
    // збігався жодного разу (`A7-02`).
    expect(decision.reason).toBe('ColumnReadOnly');
  });

  it('підказка показує причину заборони, а не просто «недоступно»', () => {
    // ⚠ Користувач, який бачить сіру комірку без пояснення, іде до
    // адміністратора, а той — до розробника.
    const decision = decide(slice({ [cellKey('R1', 'C1')]: 'PeriodClosed' }), 'R1', column());

    expect(decision.reason).toBe('PeriodClosed');

    // ⚠ Перевіряється НАЗВАНА причина, а не конкретний текст: тексти живуть у
    // каталозі на сервері (D-95), і тест, який їх повторює, ламався б від
    // кожної правки формулювання, нічого при цьому не захищаючи.
    expect(decision.hint).toContain('PeriodClosed');
    expect(decision.hint).not.toBe(decide(slice({ [cellKey('R1', 'C1')]: 'NoGrant' }), 'R1', column()).hint);
  });

  it('редагування забороненої комірки не надсилає запит на сервер', async () => {
    const send = vi.fn();
    const guard = guardOf(slice({ [cellKey('R1', 'C1')]: 'NoPermission' }));

    const reason = guard('R1', 'C1');
    if (reason === null) send();

    expect(reason).not.toBeNull();
    expect(send).not.toHaveBeenCalled();
  });

  it('обчислені комірки не редагуються', () => {
    for (const dataType of ['Formula', 'Calculated']) {
      const decision = decide(slice({}), 'R1', column({ dataType }));

      expect(decision.editable).toBe(false);
      expect(decision.reason).toBe('CalculatedCell');
    }
  });

  it('ФВ-14.2: відсутність запису у словнику прав означає ДОЗВІЛ', () => {
    // Сервер віддає лише відхилення: словник на 500×60 із дозволами на кожну
    // комірку важив би більше за самі дані.
    expect(decide(slice({}), 'R1', column()).editable).toBe(true);
  });

  it('невідома причина від сервера НЕ відкриває редагування', () => {
    // ⛔ Нова причина на сервері інакше відкривала б редагування там, де його
    // щойно заборонили.
    const decision = decide(slice({ [cellKey('R1', 'C1')]: 'СутоНоваПричина' }), 'R1', column());

    expect(decision.editable).toBe(false);

    // ⚠ І не видає її за «немає права»: підмінити невідому причину знайомою
    // означає збрехати користувачеві про те, чому комірка сіра.
    expect(decision.reason).toBeNull();

    // ⚠ Причина серверa доходить до користувача навіть без перекладу: інакше
    // нова назва перетворюється на беззмістовне «недоступно».
    expect(decision.hint).toContain('СутоНоваПричина');
  });

  it('усі причини сервера мають власний текст, а не спільну заглушку', () => {
    // Перелік звіряє з `EditDenyReason` архітектурний тест
    // `Кожна_причина_заборони_має_підказку_на_клієнті`; тут перевіряється, що
    // тексти РІЗНІ — інакше «є підказка» означало б те саме «недоступно».
    const reasons = ['PeriodClosed', 'DocumentSubmitted', 'SimulationReadOnly', 'ProjectArchived'];

    const hints = reasons.map(
      (reason) => decide(slice({ [cellKey('R1', 'C1')]: reason }), 'R1', column()).hint,
    );

    expect(new Set(hints).size).toBe(reasons.length);
  });

  it('Q-191: серверна причина переважає локальну евристику "обчислена колонка"', () => {
    // Колонка обчислювана (локально дало б `CalculatedCell`), але сервер уже
    // виніс причину вищого пріоритету за повною чергою `EditRules.CanEdit`
    // (документ поданий — перевіряється РАНІШЕ за `CalculatedCell`). Клієнт
    // мусить показати СЕРВЕРНУ причину, а не свою локальну здогадку.
    const data = slice(
      { [cellKey('R1', 'C1')]: 'DocumentSubmitted' },
      [column({ dataType: 'Formula' })],
    );

    const decision = decide(data, 'R1', column({ dataType: 'Formula' }));

    expect(decision.reason).toBe('DocumentSubmitted');
    expect(decision.reason).not.toBe('CalculatedCell');
  });

  it('Q-191: серверна причина переважає локальну евристику "read-only колонка"', () => {
    // Той самий випадок для другої локальної евристики: колонка read-only
    // (локально дало б `ColumnReadOnly`), а проєкт архівований — причина, що
    // стоїть значно вище в черзі сервера.
    const data = slice(
      { [cellKey('R1', 'C1')]: 'ProjectArchived' },
      [column({ isReadOnly: true })],
    );

    const decision = decide(data, 'R1', column({ isReadOnly: true }));

    expect(decision.reason).toBe('ProjectArchived');
    expect(decision.reason).not.toBe('ColumnReadOnly');
  });

  it('сторож вставки і сіра комірка ніколи не розходяться', () => {
    // Один предикат на обидві поведінки: інакше «комірка сіра» і «сюди не
    // вставиться» відповідали б різними правилами.
    const data = slice({ [cellKey('R1', 'C1')]: 'DocumentSubmitted' });

    expect(guardOf(data)('R1', 'C1')).toBe(decide(data, 'R1', column()).hint);
  });
});
