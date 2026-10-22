import { describe, expect, it, vi } from 'vitest';
import { EcrApiError } from '@/api/client';
import type { PermitGroupDto } from '../groupsApi';
import {
  compact,
  createBody,
  draftOf,
  draftProblems,
  emptyDraft,
  fieldOfMessageKey,
  filterGroups,
  groupName,
  groupsStats,
  isValidGroupCode,
  messageKeyOf,
  metaChanged,
  typesBody,
  typesChanged,
  updateBody,
} from '../groupsModel';

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => key,
  language: () => 'en',
}));

function group(over: Partial<PermitGroupDto> = {}): PermitGroupDto {
  return {
    code: 'WATER',
    descriptionL10n: { en: 'All water permits' },
    isActive: true,
    nameL10n: { en: 'Water permits', ru: 'Водные разрешения' },
    registryCode: 'PRMG_WATER',
    registryDefId: 90,
    rowVersion: 'v1',
    types: [
      { code: 'WaterIntake', category: 'Water', headerRegistryCode: 'PRM_WaterIntake', nameL10n: { en: 'Water intake' } },
      { code: 'Discharge', category: 'Water', headerRegistryCode: 'PRM_Discharge', nameL10n: { en: 'Discharge' } },
    ],
    updatedAt: '2026-10-20T10:00:00Z',
    usedInColumns: 0,
    usedInTemplates: 0,
    ...over,
  };
}

describe('groupsModel', () => {
  it('код групи: літера, далі літери/цифри/_, до 59 символів', () => {
    expect(isValidGroupCode('WATER_1')).toBe(true);
    expect(isValidGroupCode('A'.repeat(59))).toBe(true);
    expect(isValidGroupCode('A'.repeat(60))).toBe(false);
    expect(isValidGroupCode('1A')).toBe(false);
    expect(isValidGroupCode('A-B')).toBe(false);
    expect(isValidGroupCode('')).toBe(false);
  });

  it('назва групи - мовою користувача, порожню замінює код', () => {
    expect(groupName(group(), 'ru')).toBe('Водные разрешения');
    expect(groupName(group({ nameL10n: {} }))).toBe('WATER');
  });

  it('пошук: код, назва будь-якою мовою, опис, назва типу складу; показники active/inactive', () => {
    const groups = [group(), group({ code: 'AIR', nameL10n: { en: 'Air' }, descriptionL10n: {}, types: [], isActive: false })];

    expect(filterGroups(groups, { needle: 'водные', stat: '' }).map((item) => item.code)).toEqual(['WATER']);
    expect(filterGroups(groups, { needle: 'ALL WATER', stat: '' }).map((item) => item.code)).toEqual(['WATER']);
    expect(filterGroups(groups, { needle: 'discharge', stat: '' }).map((item) => item.code)).toEqual(['WATER']);
    expect(filterGroups(groups, { needle: '', stat: 'inactive' }).map((item) => item.code)).toEqual(['AIR']);
    expect(groupsStats(groups)).toEqual({ all: 2, active: 1, inactive: 1 });
  });

  it('порожні мови не потрапляють у запит; описа немає - null; порожній склад створення - null', () => {
    expect(compact({ en: 'A', ru: '  ', kz: '' })).toEqual({ en: 'A' });
    const draft = { ...emptyDraft(), code: ' WATER ', name: { en: 'Water', ru: '' } };

    expect(createBody(draft)).toEqual({ code: 'WATER', nameL10n: { en: 'Water' }, descriptionL10n: null, typeCodes: null });
    expect(createBody({ ...draft, typeCodes: ['A', 'B'] }).typeCodes).toEqual(['A', 'B']);
  });

  it('зміни: назва/опис/активність і склад розрізняються; порядок складу - теж зміна', () => {
    const original = draftOf(group());

    expect(metaChanged(original, original)).toBe(false);
    expect(typesChanged(original, original)).toBe(false);
    expect(metaChanged({ ...original, isActive: false }, original)).toBe(true);
    expect(metaChanged({ ...original, name: { ...original.name, ru: '' } }, original)).toBe(true);
    expect(typesChanged({ ...original, typeCodes: [...original.typeCodes].reverse() }, original)).toBe(true);
    expect(updateBody({ ...original, isActive: false }, 'v1')).toMatchObject({ isActive: false, rowVersion: 'v1' });
    expect(typesBody(original, 'v2')).toEqual({ typeCodes: ['WaterIntake', 'Discharge'], rowVersion: 'v2' });
  });

  it('дзеркало серверних перевірок: код лише при створенні, назва хоча б однією мовою, довжина опису', () => {
    expect(Object.keys(draftProblems({ ...emptyDraft(), code: '1x', name: {} }, true)).sort()).toEqual(['code', 'name']);
    expect(draftProblems({ ...emptyDraft(), code: '1x', name: { en: 'Ok' } }, false)).toEqual({});
    expect(Object.keys(draftProblems({ ...emptyDraft(), code: 'OK', name: { en: 'Ok' }, description: { en: 'x'.repeat(1001) } }, true))).toEqual([
      'description',
    ]);
  });

  it('messageKey відмови -> поле форми; невідомий ключ - відмова всієї форми', () => {
    expect(fieldOfMessageKey('err.ECR-PRM-4097.code')).toBe('code');
    expect(fieldOfMessageKey('err.ECR-PRM-4097.registryCode')).toBe('code');
    expect(fieldOfMessageKey('err.ECR-PRM-0422.permitGroupNameRequired')).toBe('name');
    expect(fieldOfMessageKey('err.ECR-PRM-0422.permitGroupMemberUnknown')).toBe('types');
    expect(fieldOfMessageKey('err.ECR-PRM-0422.permitGroupTextTooLong')).toBeNull();
    expect(fieldOfMessageKey('err.ECR-SYS-0503.databaseBusy')).toBeNull();
  });

  it('messageKeyOf: ключ і підстановки без технічних полів; не відмова API - null', () => {
    const error = new EcrApiError({
      title: 'x',
      status: 409,
      errorCode: 'ECR-PRM-4097',
      correlationId: 'c',
      extensions2: { messageKey: 'err.ECR-PRM-4097.code', errorCode: 'ECR-PRM-4097', correlationId: 'c', code: 'WATER', n: 2 },
    });

    expect(messageKeyOf(error)).toEqual({ key: 'err.ECR-PRM-4097.code', params: { code: 'WATER', n: '2' } });
    expect(messageKeyOf(new Error('boom'))).toBeNull();
  });
});
