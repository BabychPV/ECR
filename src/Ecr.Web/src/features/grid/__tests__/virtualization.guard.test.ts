import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Віртуалізацію сітки не вимкнено (`ФВ-14.29`).
 *
 * ⚠ Сам доказ «500×60 малює лише вікно» — у браузері
 * (`e2e/virtualGrid.spec.ts`: 300 комірок у DOM із 30 000; з
 * `disableVirtualX/Y` — 30 000, червоне). Там міряється СТЕНД з параметрами
 * `DocumentGrid`. Цей сторож — у CI на кожен пуш — тримає зв'язок стенда з
 * продуктом: жодна сітка не вимикає віртуалізацію, і стенд не розходиться з
 * `DocumentGrid` у параметрах, від яких залежить кількість змонтованих рядків.
 */
const read = (file: string): string => readFileSync(path.resolve(process.cwd(), 'src', file), 'utf8');

const Product = read('features/grid/DocumentGrid.tsx');
const Stand = read('pages/VirtualGridStandPage.tsx');

describe('ФВ-14.29: віртуалізація сітки', () => {
  it.each([
    ['DocumentGrid', Product],
    ['стенд', Stand],
  ])('%s не вимикає віртуалізацію', (_name, code) => {
    expect(code).not.toMatch(/disableVirtual[XY]/);
  });

  it.each(['theme="compact"', 'rowSize={rowSize}', "style={{ height: '70vh' }}"])(
    'стенд і DocumentGrid мають однаковий %s',
    (prop) => {
      expect(Product).toContain(prop);
      expect(Stand).toContain(prop);
    },
  );
});
