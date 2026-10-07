import { describe, expect, it } from 'vitest';
import type { RoleView } from '@/api/types';
import { roleLabel } from '@/features/security/roleLabel';

/**
 * Назва ролі з `GET /roles` (`nameL10n`) замість коду.
 *
 * ⚠ МУТАЦІЯ: повернути `role?.code` (як до фіксу) — червоніє перший випадок.
 */

const base: RoleView = {
  id: 3,
  code: 'FlareOps',
  isActive: true,
  isBuiltIn: false,
  permissions: [],
  dangerousPermissions: [],
};

describe('roleLabel', () => {
  it('бере назву з відповіді', () => {
    expect(roleLabel({ ...base, nameL10n: { values: { en: 'Flare operators' } } })).toBe('Flare operators');
  });

  it('без назви — код', () => {
    expect(roleLabel(base)).toBe('FlareOps');
    expect(roleLabel({ ...base, nameL10n: null })).toBe('FlareOps');
    expect(roleLabel({ ...base, nameL10n: { values: { en: '' } } })).toBe('FlareOps');
  });

  it('ролі немає — порожньо', () => {
    expect(roleLabel(undefined)).toBe('');
  });
});
