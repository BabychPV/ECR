import { vi } from 'vitest';

/**
 * Таблиця місяців на вкладці Periods лежить у згорнутому «Details» (пам'ять — `ecr.note.periods-details.<user>`).
 * Тести рядків і дій над ними починають із розгорнутого вибору людини, не залежно від того, під яким користувачем.
 */
export function openPeriodDetails(): void {
  const real = Storage.prototype.getItem;
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(function (this: Storage, key: string) {
    return key.startsWith('ecr.note.periods-details.') ? '0' : real.call(this, key);
  });
}
