import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import {
  createPermitGroup,
  deletePermitGroup,
  listPermitGroups,
  replacePermitGroupTypes,
  updatePermitGroup,
  type CreatePermitGroupRequest,
  type PermitGroupDto,
  type ReplacePermitGroupTypesRequest,
  type UpdatePermitGroupRequest,
} from './groupsApi';

/** Ключі кешу екрана «Groups». Власні, щоб не чіпати спільний `queryKeys.ts`. */
export const groupsKeys = {
  all: ['permits', 'groups'] as const,
  list: ['permits', 'groups', 'list'] as const,
};

/** Перелік груп: ОДИН запит (`GET /permit-groups`), у відповіді вже склад і токен кожної. */
export function usePermitGroups() {
  return useQuery<PermitGroupDto[]>({ queryKey: groupsKeys.list, queryFn: listPermitGroups });
}

/** Скидає перелік груп і цілі Lookup шаблону: склад, назва, опис та активність групи видимі в обох. */
function useInvalidateGroups(): (code?: string) => Promise<void> {
  const client = useQueryClient();

  return async (code) => {
    await Promise.all([
      // Піклер групи (записи `PRMG_<код>`, свіжість 5 хв): склад змінився - відкритий документ не має показувати старий.
      ...(code === undefined ? [] : [client.invalidateQueries({ queryKey: queryKeys.registries.entries(`PRMG_${code}`) })]),
      client.invalidateQueries({ queryKey: groupsKeys.all }),
      // Цілі Lookup (шапки + групи) читає `lookupTargetsQuery`: назва, опис і активність групи видимі й там.
      client.invalidateQueries({ queryKey: queryKeys.registries.lookupTargets() }),
      // «Unlisted» поточна ціль (`GET /registries/by-id/{id}`, свіжість 5 хв): назва/опис/активність групи змінились -
      // відкрите поле шапки чи колонки не має показувати старі. Префікс - усі id (група в запиті може бути будь-яка).
      client.invalidateQueries({ queryKey: ['registries', 'target'] }),
    ]);
  };
}

export function useCreateGroup() {
  const invalidate = useInvalidateGroups();

  return useMutation<PermitGroupDto, Error, CreatePermitGroupRequest>({
    mutationKey: ['permits', 'groups', 'create'],
    mutationFn: (body) => createPermitGroup(body),
    onSuccess: () => invalidate(),
  });
}

export interface UpdateGroupVariables {
  readonly code: string;
  readonly body: UpdatePermitGroupRequest;
}

export function useUpdateGroup() {
  const invalidate = useInvalidateGroups();

  return useMutation<PermitGroupDto, Error, UpdateGroupVariables>({
    mutationKey: ['permits', 'groups', 'update'],
    mutationFn: ({ code, body }) => updatePermitGroup(code, body),
    onSuccess: (_saved, { code }) => invalidate(code),
  });
}

export interface ReplaceTypesVariables {
  readonly code: string;
  readonly body: ReplacePermitGroupTypesRequest;
}

export function useReplaceGroupTypes() {
  const invalidate = useInvalidateGroups();

  return useMutation<PermitGroupDto, Error, ReplaceTypesVariables>({
    mutationKey: ['permits', 'groups', 'types'],
    mutationFn: ({ code, body }) => replacePermitGroupTypes(code, body),
    onSuccess: (_saved, { code }) => invalidate(code),
  });
}

export function useDeleteGroup() {
  const invalidate = useInvalidateGroups();

  return useMutation<void, Error, string>({
    mutationKey: ['permits', 'groups', 'delete'],
    mutationFn: (code) => deletePermitGroup(code),
    onSuccess: (_done, code) => invalidate(code),
  });
}
