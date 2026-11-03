import { useMemo, useState, type JSX } from 'react';
import { Modal, NativeSelect } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import { toTreeLevels } from '@/features/registries/tree/treeAdapter';
import { getRegistryHierarchy } from '@/features/registries/tree/treeApi';
import { t } from '@/shared/i18n';
import { childLevelsFor, type TreeLevel, type TreeNode } from '@/shared/ui/TreeEditor';
import { NodeAttributesForm } from './NodeAttributesForm';

export interface CreateNodeModalProps {
  readonly registryCode: string;
  readonly registryId: number;
  /** Код запису видає система (`CodeMode = Auto`). */
  readonly codeAutomatic: boolean;
  readonly fields: readonly components['schemas']['RegistryFieldDto'][];
  /** Куди додається вузол; `null` - діалог закритий. */
  readonly draft: { readonly parent: TreeNode | null; readonly level: TreeLevel } | null;
  readonly onClose: () => void;
  readonly onCreated: (id: number) => void;
  /** Перейти до вузла, що тримає номер NAE (діалог при цьому закривається сторінкою). */
  readonly onOpenNode?: ((id: number) => void) | undefined;
}

/**
 * Діалог нового вузла. ⚠ Сторінка тягне його через `React.lazy()`: Mantine `Modal` і поля форми вантажаться окремим
 * чанком, коли людина вперше натисне «Add child» (бюджет `D-132`).
 *
 * Тип нового вузла (рівень) - вибір серед рівнів, дозволених під цим батьком (порядок рівнів, вкладення рівня в себе);
 * форма під ним показує лише поля вибраного типу.
 */
export function CreateNodeModal({
  registryCode,
  registryId,
  codeAutomatic,
  fields,
  draft,
  onClose,
  onCreated,
  onOpenNode,
}: CreateNodeModalProps): JSX.Element {
  const hierarchy = useQuery({
    queryKey: queryKeys.registries.hierarchy(registryCode),
    queryFn: () => getRegistryHierarchy(registryCode),
    enabled: draft !== null,
  });
  const [chosen, setChosen] = useState<string | null>(null);

  const options = useMemo(() => {
    if (draft === null) return [];
    const all = toTreeLevels(hierarchy.data?.levels ?? []);
    const allowed = childLevelsFor(all, draft.parent?.levelCode ?? null);

    return allowed.some((level) => level.code === draft.level.code) ? allowed : [draft.level, ...allowed];
  }, [draft, hierarchy.data]);

  const level = draft === null ? null : (options.find((item) => item.code === (chosen ?? draft.level.code)) ?? draft.level);

  // ⛔ Будь-яке закриття (X, Esc, «Cancel», перехід до тримача номера) скидає вибраний тип: інакше наступне «Add child»
  // відкрилося б із типом минулого разу, а не з типом, який пропонує батько.
  const close = (): void => {
    setChosen(null);
    onClose();
  };

  return (
    <Modal
      opened={draft !== null}
      onClose={close}
      title={t('permits.nae.form.newNode')}
      size="lg"
    >
      {draft !== null && level !== null && (
        <>
          {options.length > 1 && (
            <NativeSelect
              mb="sm"
              label={t('permits.nae.form.nodeType')}
              description={t('permits.nae.form.nodeTypeHint')}
              data={options.map((item) => ({ value: item.code, label: item.name }))}
              value={level.code}
              onChange={(event) => setChosen(event.currentTarget.value)}
              data-nae-node-type=""
            />
          )}
          <NodeAttributesForm
            registryCode={registryCode}
            registryId={registryId}
            codeAutomatic={codeAutomatic}
            fields={fields}
            target={{ mode: 'create', parent: draft.parent, level }}
            parentPath={draft.parent === null ? [] : [draft.parent]}
            readOnly={false}
            onSaved={(id) => {
              setChosen(null);
              onCreated(id);
            }}
            onCancel={close}
            onOpenNode={
              onOpenNode === undefined
                ? undefined
                : (id) => {
                    setChosen(null);
                    onOpenNode(id);
                  }
            }
          />
        </>
      )}
    </Modal>
  );
}
