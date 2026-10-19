import { describe, expect, it } from 'vitest';
import { checkNewTypeName, deriveTypeCode, isValidTypeCode, normalizeTypeName } from '../typeCode';
import { FixtureTypes } from './typesFixture';

/**
 * Код типу з назви й перевірка назви майстра (PM-08a).
 *
 * Мутаційний доказ (вручну, відновлено): без останнього `.replace(/ +/g, '')` у `deriveTypeCode` падають
 * «Noise permit → NoisePermit» і «код завжди підходить серверу»; перевірка дубля назви без `toLocaleLowerCase`
 * валить «дубль назви не залежить від регістру»; зняття перевірки `isValidTypeCode` у `checkNewTypeName` валить
 * «кирилична назва».
 */
describe('deriveTypeCode', () => {
  it.each([
    ['Noise permit', 'NoisePermit'],
    ['noise permit', 'NoisePermit'],
    ['  Air   emission  permit ', 'AirEmissionPermit'],
    ['NOx limits', 'NOxLimits'],
    ['Water discharge (offshore)', 'WaterDischargeOffshore'],
    ['Permit 2026', 'Permit2026'],
    ['Noise #', 'Noise'],
    ['Шум noise', 'Noise'],
    ['Шум', ''],
    ['', ''],
  ])('%j → %j', (name, code) => {
    expect(deriveTypeCode(name)).toBe(code);
  });

  it('код, який вийшов непорожнім і з літери, завжди підходить серверу', () => {
    for (const name of ['Noise permit', 'a b c', 'X 1 y 2', 'Water discharge (offshore) №3']) {
      const code = deriveTypeCode(name);
      expect(isValidTypeCode(code), `${name} → ${code}`).toBe(true);
    }
  });
});

describe('isValidTypeCode', () => {
  it.each([
    ['A', true],
    ['Air_Emission2', true],
    ['A'.repeat(64), true],
    ['A'.repeat(65), false],
    ['', false],
    ['2Air', false],
    ['_Air', false],
    ['Air Emission', false],
    ['Повітря', false],
  ])('%j → %s', (code, expected) => {
    expect(isValidTypeCode(code)).toBe(expected);
  });
});

describe('checkNewTypeName', () => {
  it('порожня або пробіли → required', () => {
    expect(checkNewTypeName('', FixtureTypes)).toBe('required');
    expect(checkNewTypeName('   ', FixtureTypes)).toBe('required');
  });

  it('дубль назви не залежить від регістру й крайніх пробілів, а також мови', () => {
    expect(checkNewTypeName('air emission permit', FixtureTypes)).toBe('nameTaken');
    expect(checkNewTypeName('  Air Emission Permit  ', FixtureTypes)).toBe('nameTaken');
    expect(checkNewTypeName('разрешение на сбросы', FixtureTypes)).toBe('nameTaken');
  });

  it('кирилична назва без жодної латинської літери → codeInvalid', () => {
    expect(checkNewTypeName('Дозвіл на шум', FixtureTypes)).toBe('codeInvalid');
  });

  it('назва з цифри → codeInvalid', () => {
    expect(checkNewTypeName('2026 noise', FixtureTypes)).toBe('codeInvalid');
  });

  it('інша назва, що дає зайнятий код → codeTaken (без урахування регістру)', () => {
    expect(checkNewTypeName('Water Discharge', FixtureTypes)).toBe('codeTaken');
    expect(checkNewTypeName('waste storage', FixtureTypes)).toBe('codeTaken');
  });

  it('вільна назва → null', () => {
    expect(checkNewTypeName('Noise permit', FixtureTypes)).toBeNull();
    expect(checkNewTypeName('Noise permit', [])).toBeNull();
  });
});

describe('normalizeTypeName', () => {
  it('обрізає пробіли й знижує регістр', () => {
    expect(normalizeTypeName('  Noise PERMIT ')).toBe('noise permit');
  });
});
