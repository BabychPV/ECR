import { describe, expect, it } from 'vitest';
import type { RegistryEntryDto } from '@/api/types';
import { entryLabelsOf } from '../lookupEntryLabels';

function entry(over: Partial<RegistryEntryDto> & { id: number; display: string }): RegistryEntryDto {
  return { code: String(over.id), parentEntryId: null, validFrom: null, validTo: null, ...over };
}

describe('entryLabelsOf', () => {
  it('звичайний довідник і шапка типу: підпис - display як є', () => {
    const labels = entryLabelsOf([entry({ id: 1, display: 'Nitrogen' }), entry({ id: 2, display: 'KZ79-0001' })]);

    expect(labels.get(1)).toBe('Nitrogen');
    expect(labels.get(2)).toBe('KZ79-0001');
  });

  it('піклер групи: «номер — тип»; тип за назвою, інакше за кодом', () => {
    const labels = entryLabelsOf([
      entry({ id: 1, display: 'N-1', typeCode: 'WaterIntake', typeName: 'Water intake' }),
      entry({ id: 2, display: 'N-2', typeCode: 'Discharge' }),
    ]);

    expect(labels.get(1)).toBe('N-1 — Water intake');
    expect(labels.get(2)).toBe('N-2 — Discharge');
  });

  it('той самий номер і тип з різним початком чинності розрізняє дата; різні типи - ні', () => {
    const labels = entryLabelsOf([
      entry({ id: 1, display: 'N-1', typeName: 'Water intake', validFrom: '2025-01-01' }),
      entry({ id: 2, display: 'N-1', typeName: 'Water intake', validFrom: '2026-01-01' }),
      entry({ id: 3, display: 'N-1', typeName: 'Discharge', validFrom: '2026-01-01' }),
    ]);

    expect(labels.get(1)).toBe('N-1 — Water intake (2025-01-01)');
    expect(labels.get(2)).toBe('N-1 — Water intake (2026-01-01)');
    expect(labels.get(3)).toBe('N-1 — Discharge');
  });

  it('мемоїзація за масивом: той самий масив - та сама мапа', () => {
    const entries = [entry({ id: 1, display: 'A' })];

    expect(entryLabelsOf(entries)).toBe(entryLabelsOf(entries));
  });
});
