import type { JSX } from 'react';
import { Modal } from '@mantine/core';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import type { TreeLevel, TreeNode } from '@/shared/ui/TreeEditor';
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
}

/**
 * Діалог нового вузла. ⚠ Сторінка тягне його через `React.lazy()`: Mantine `Modal` і поля форми вантажаться окремим
 * чанком, коли людина вперше натисне «Add child» (бюджет `D-132`).
 */
export function CreateNodeModal({
  registryCode,
  registryId,
  codeAutomatic,
  fields,
  draft,
  onClose,
  onCreated,
}: CreateNodeModalProps): JSX.Element {
  return (
    <Modal opened={draft !== null} onClose={onClose} title={t('permits.nae.form.newNode')} size="lg">
      {draft !== null && (
        <NodeAttributesForm
          registryCode={registryCode}
          registryId={registryId}
          codeAutomatic={codeAutomatic}
          fields={fields}
          target={{ mode: 'create', parent: draft.parent, level: draft.level }}
          parentPath={draft.parent === null ? [] : [draft.parent]}
          readOnly={false}
          onSaved={onCreated}
          onCancel={onClose}
        />
      )}
    </Modal>
  );
}
