import { describe, expect, it } from 'vitest';
import { capabilities, expiringIn, ExpiringWithinDays } from '../permitRules';

describe('capabilities: що можна робити зі станом', () => {
  it('чернетка: шапка, рядки, Activate і Delete; Renew та End — ні', () => {
    expect(capabilities('Draft', true)).toEqual({
      editHeader: true,
      editRows: true,
      activate: true,
      end: false,
      renew: false,
      remove: true,
    });
  });

  it('чинний: шапка, рядки, End і Renew; Activate та Delete — ні', () => {
    expect(capabilities('Active', true)).toEqual({
      editHeader: true,
      editRows: true,
      activate: false,
      end: true,
      renew: true,
      remove: false,
    });
  });

  it('закінчений: лише читання й Renew (правило інтерфейсу, сервер рядки не блокує)', () => {
    expect(capabilities('Expired', true)).toEqual({
      editHeader: false,
      editRows: false,
      activate: false,
      end: false,
      renew: true,
      remove: false,
    });
  });

  it('без права запису вимкнено все, що пише, у будь-якому стані', () => {
    for (const status of ['Draft', 'Active', 'Expired']) {
      expect(Object.values(capabilities(status, false)).every((allowed) => !allowed), status).toBe(true);
    }
  });

  it('невідомий стан нічого не дозволяє змінювати шапці, але й не закриває рядки', () => {
    const unknown = capabilities('Suspended', true);
    expect(unknown.editHeader).toBe(false);
    expect(unknown.activate).toBe(false);
  });
});

describe('expiringIn', () => {
  it('чинний у межах попередження — скільки днів лишилось', () => {
    expect(expiringIn({ status: 'Active', endsInDays: ExpiringWithinDays })).toBe(ExpiringWithinDays);
    expect(expiringIn({ status: 'Active', endsInDays: 3 })).toBe(3);
  });

  it('далі за межу, чернетка чи закінчений — нічого', () => {
    expect(expiringIn({ status: 'Active', endsInDays: ExpiringWithinDays + 1 })).toBeNull();
    expect(expiringIn({ status: 'Draft', endsInDays: 5 })).toBeNull();
    expect(expiringIn({ status: 'Active', endsInDays: null })).toBeNull();
  });
});
