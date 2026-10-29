import { useMemo, useState } from 'react';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { todayDateOnly } from '@/shared/format/dateOnly';
import type { TreeLevel, TreeMoveRequest, TreeNode } from '@/shared/ui/TreeEditor';
import { showApiError } from '@/shared/ui/notify';
import { toTreeLevels, toTreeNodes } from './treeAdapter';
import { getRegistryHierarchy, getRegistryTree, moveRegistryEntry, retireRegistryEntry } from './treeApi';

/**
 * Дані й дії редактора дерева ієрархічного довідника (NAE plan N1 -> `shared/ui/TreeEditor`).
 *
 * ⛔ `structuralSharing: false` на дереві - свідомо (урок `features/grid/sliceCache.ts`): `replaceEqualDeep` обходить
 * ВЕСЬ новий результат, щоб зберегти посилання, а дерево - це плаский список у десятки тисяч вузлів; кожен перезапит
 * (набір у пошуку!) заморожував би вкладку. Нова відповідь просто замінює стару; редактор усе одно індексує список заново.
 *
 * ⚠ `placeholderData: keepPreviousData`: під час набору пошуку дерево не блимає порожнечею між запитами.
 *
 * ⚠ Зміни мають `onError` (власне сповіщення про відмову): хук не показує помилку сам у рендері, тож подвійного тосту нема.
 */
export function useRegistryTree(registryCode: string): {
  readonly nodes: readonly TreeNode[];
  readonly levels: readonly TreeLevel[];
  readonly isHierarchical: boolean;
  readonly isPending: boolean;
  readonly isFetching: boolean;
  readonly error: unknown;
  readonly refetch: () => void;
  readonly query: string;
  readonly setQuery: (value: string) => void;
  readonly showRetired: boolean;
  readonly setShowRetired: (value: boolean) => void;
  readonly move: (request: TreeMoveRequest) => Promise<void>;
  readonly retire: (node: TreeNode, cascade: boolean) => Promise<void>;
  readonly isMutating: boolean;
} {
  const queryClient = useQueryClient();
  const [query, setQuery] = useState('');
  const [showRetired, setShowRetired] = useState(false);

  const hierarchy = useQuery({
    queryKey: queryKeys.registries.hierarchy(registryCode),
    queryFn: () => getRegistryHierarchy(registryCode),
  });

  const tree = useQuery({
    queryKey: queryKeys.registries.tree(registryCode, showRetired, query),
    queryFn: () => getRegistryTree(registryCode, showRetired, query),
    structuralSharing: false,
    placeholderData: keepPreviousData,
    refetchOnWindowFocus: false,
  });

  const nodes = useMemo(() => toTreeNodes(tree.data?.nodes ?? [], todayDateOnly()), [tree.data]);
  const levels = useMemo(() => toTreeLevels(hierarchy.data?.levels ?? tree.data?.levels ?? []), [hierarchy.data, tree.data]);

  const refresh = async (): Promise<void> => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: queryKeys.registries.tree(registryCode) }),
      queryClient.invalidateQueries({ queryKey: queryKeys.registries.hierarchy(registryCode) }),
      queryClient.invalidateQueries({ queryKey: queryKeys.registries.entries(registryCode) }),
    ]);
  };

  const moveMutation = useMutation({
    mutationFn: (request: TreeMoveRequest) =>
      moveRegistryEntry(registryCode, Number(request.id), {
        parentEntryId: request.parentId === null ? null : Number(request.parentId),
        levelCode: null,
        beforeEntryId: request.beforeId === null ? null : Number(request.beforeId),
      }),
    onSuccess: refresh,
    onError: showApiError,
  });

  const retireMutation = useMutation({
    mutationFn: (input: { readonly id: number; readonly cascade: boolean }) =>
      retireRegistryEntry(registryCode, input.id, { on: null, cascade: input.cascade }),
    onSuccess: refresh,
    onError: showApiError,
  });

  return {
    nodes,
    levels,
    isHierarchical: tree.data?.isHierarchical ?? hierarchy.data?.isHierarchical ?? true,
    isPending: tree.isPending,
    isFetching: tree.isFetching,
    error: tree.error ?? hierarchy.error,
    refetch: () => {
      void tree.refetch();
      void hierarchy.refetch();
    },
    query,
    setQuery,
    showRetired,
    setShowRetired,
    move: async (request) => {
      await moveMutation.mutateAsync(request);
    },
    retire: async (node, cascade) => {
      await retireMutation.mutateAsync({ id: Number(node.id), cascade });
    },
    isMutating: moveMutation.isPending || retireMutation.isPending,
  };
}
