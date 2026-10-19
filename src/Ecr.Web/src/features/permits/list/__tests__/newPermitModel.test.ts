import { describe, expect, it } from 'vitest';
import { makeType } from '../../types/__tests__/typesFixture';
import {
  checkDetails,
  checkType,
  defaultValidity,
  initialData,
  isoDate,
  publishedTypes,
  toRequest,
  type NewPermitData,
} from '../newPermitModel';

/**
 * Модель майстра «New permit» (PM-07). Мутаційний доказ (вручну, відновлено): `<=` на `<` у `checkDetails`
 * пропускає однакові дати (валить «кінець не після початку»); без `trim()` у `toRequest` у запит їде «  KZ  ».
 */
const valid: NewPermitData = {
  type: 'AirEmission',
  number: 'KZ-1',
  location: '',
  validFrom: '2026-01-01',
  validTo: '2027-01-01',
};

describe('checkDetails', () => {
  it('нормальні дані — без відмови', () => {
    expect(checkDetails(valid)).toBeNull();
  });

  it('порожній чи пробільний номер', () => {
    expect(checkDetails({ ...valid, number: '' })).toBe('numberRequired');
    expect(checkDetails({ ...valid, number: '   ' })).toBe('numberRequired');
  });

  it('порожня чи недописана дата', () => {
    expect(checkDetails({ ...valid, validFrom: '' })).toBe('datesRequired');
    expect(checkDetails({ ...valid, validTo: '2027-1-1' })).toBe('datesRequired');
  });

  it('кінець не після початку: рівні й перевернуті дати відхиляються', () => {
    expect(checkDetails({ ...valid, validTo: valid.validFrom })).toBe('validityOrder');
    expect(checkDetails({ ...valid, validTo: '2025-12-31' })).toBe('validityOrder');
  });
});

describe('типи', () => {
  const types = [
    makeType('AirEmission'),
    makeType('WasteStorage', { status: 'Draft' }),
    makeType('WaterDischarge', { hasUnpublishedChanges: true }),
  ];

  it('пропонуються лише опубліковані (зі змінами в чернетці — теж)', () => {
    expect(publishedTypes(types).map((item) => item.code)).toEqual(['AirEmission', 'WaterDischarge']);
  });

  it('неопублікований тип обрати не можна, опублікований — можна', () => {
    expect(checkType({ ...valid, type: 'WasteStorage' }, types)).toBe('typeRequired');
    expect(checkType({ ...valid, type: '' }, types)).toBe('typeRequired');
    expect(checkType({ ...valid, type: 'WaterDischarge' }, types)).toBeNull();
  });

  it('початкові дані: перший опублікований тип; без таких — порожньо', () => {
    const today = new Date(2026, 9, 19);
    expect(initialData(today, types).type).toBe('AirEmission');
    expect(initialData(today, [makeType('X', { status: 'Draft' })]).type).toBe('');
  });
});

describe('дати за замовчуванням', () => {
  it('від сьогодні рівно на рік, за локальним календарем', () => {
    expect(defaultValidity(new Date(2026, 9, 19))).toEqual({ validFrom: '2026-10-19', validTo: '2027-10-19' });
    expect(isoDate(new Date(2026, 0, 5))).toBe('2026-01-05');
  });
});

describe('toRequest', () => {
  it('обрізає пробіли, порожнє розташування стає null, решта як є', () => {
    expect(toRequest({ ...valid, number: '  KZ-1 ', location: '  ' })).toEqual({
      typeCode: 'AirEmission',
      number: 'KZ-1',
      location: null,
      issueDate: null,
      validFrom: '2026-01-01',
      validTo: '2027-01-01',
      reportingFlags: null,
    });
    expect(toRequest({ ...valid, location: ' Offshore ' }).location).toBe('Offshore');
  });
});
