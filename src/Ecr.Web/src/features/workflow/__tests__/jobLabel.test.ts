import { describe, it, expect } from 'vitest';
import { humanizeJobId, jobKindLabel } from '../jobLabel';

/**
 * Аудит-пас 8, lane6, п.8: тости й перелік `/admin/jobs` показували сирі
 * .NET-імена буквально — `IRecalculationJob#...`,
 * `Ecr.Application.Ports.IRecalculationJob`. Фікс — косметичний рендеринг,
 * без зміни `jobId`/`jobCode` у сховищі чи API.
 *
 * ⛔ Ці тести не завантажують каталог рядків (як і решта клієнтських
 * компонентних тестів) — `t()` тому повертає позначений ключ
 * (`⟦jobs.kind.recalculation⟧`), а не готовий переклад. Мутаційний доказ той
 * самий: важливо, що функція ПОВЕРНУЛА ЩОСЬ ІНШЕ, ніж сирий `.NET`-тип, а не
 * конкретний переклад (той доводить сам каталог — `09-seed.sql`).
 */
describe('jobKindLabel', () => {
  it("перетворює повне ім'я типу на ключ каталогу", () => {
    expect(jobKindLabel('Ecr.Application.Ports.IRecalculationJob')).toBe(
      '⟦jobs.kind.recalculation⟧',
    );
  });

  it('перетворює просте ім\'я типу (без namespace) так само', () => {
    expect(jobKindLabel('ICollectionJob')).toBe('⟦jobs.kind.collection⟧');
  });

  it('невідомий тип — повертає просте ім\'я, а не вигадує підпис', () => {
    expect(jobKindLabel('Ecr.Application.Ports.ISomeNewJob')).toBe('ISomeNewJob');
  });
});

describe('humanizeJobId', () => {
  it('лишає GUID екземпляра, заміняючи лише тип на людський підпис', () => {
    const guid = 'a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4';
    expect(humanizeJobId(`IRecalculationJob-${guid}`)).toBe(`⟦jobs.kind.recalculation⟧-${guid}`);
  });

  it(
    // ⛔ Мутаційний доказ: рядок, що НЕ відповідає формату `Тип-GUID32`
    // (повторювана задача, `Тип:відбиток`), повертається БЕЗ ЗМІН — інакше
    // регулярний вираз, ослаблений до «будь-що з дефісом», підмінив би
    // невідомий формат вигаданим текстом.
    'невідомий формат (повторювана задача) — без змін',
    () => {
      expect(humanizeJobId('IFormulaRecalculationJob:9f8e7d')).toBe(
        'IFormulaRecalculationJob:9f8e7d',
      );
    },
  );

  it('невідомий тип у знайомому форматі — jobId без змін', () => {
    const guid = 'a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4';
    expect(humanizeJobId(`ISomeNewJob-${guid}`)).toBe(`ISomeNewJob-${guid}`);
  });
});
