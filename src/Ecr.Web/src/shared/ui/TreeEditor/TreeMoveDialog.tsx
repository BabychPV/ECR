import { useMemo, useState, type JSX } from 'react';
import { Button, Group, Modal, Radio, Stack, Text, TextInput } from '@mantine/core';
import { t } from '@/shared/i18n';
import { moveTargets, type TreeIndex, type TreeLevel, type TreeNode, type TreeNodeId } from './treeModel';

/** Скільки місць показувати одразу: перелік не віртуалізований, довшого за це людина не прочитає - звужує пошуком. */
export const MoveTargetLimit = 50;

/** Значення «стати коренем» у групі радіокнопок (id вузла рядком не буває порожнім). */
const RootValue = '';

export interface TreeMoveDialogProps {
  readonly node: TreeNode;
  readonly index: TreeIndex;
  readonly levels: readonly TreeLevel[];
  readonly busy: boolean;
  readonly onMove: (parentId: TreeNodeId | null) => void;
  readonly onClose: () => void;
}

/**
 * «Перенести до…»: безмишевий еквівалент перетягування. Перелік - лише місця, дозволені порядком рівнів
 * (`moveTargets`); сервер однаково перевіряє (422), але недозволене тут не пропонується.
 *
 * ⚠ Монтується лише відкритим: вибір і пошук не переходять на наступне відкриття.
 */
export function TreeMoveDialog({ node, index, levels, busy, onMove, onClose }: TreeMoveDialogProps): JSX.Element {
  const [query, setQuery] = useState('');
  const [choice, setChoice] = useState<string | null>(null);

  const targets = useMemo(() => moveTargets(index, levels, node, query, MoveTargetLimit), [index, levels, node, query]);
  const currentParent = node.parentId !== null && index.byId.has(node.parentId) ? node.parentId : null;
  const currentValue = currentParent === null ? RootValue : String(currentParent);
  const nothing = !targets.rootAllowed && targets.items.length === 0;

  const confirm = (): void => {
    if (choice === null) return;
    if (choice === RootValue) {
      onMove(null);
      return;
    }
    const picked = targets.items.find((item) => String(item.node.id) === choice);
    if (picked !== undefined) onMove(picked.node.id);
  };

  return (
    <Modal opened onClose={onClose} title={t('treeEditor.move.title', { name: node.label })} size="lg">
      <Stack gap="sm" data-testid="tree-move-dialog">
        <Text size="sm" c="dimmed">
          {t('treeEditor.move.hint')}
        </Text>

        <TextInput
          label={t('treeEditor.move.search')}
          value={query}
          onChange={(event) => setQuery(event.currentTarget.value)}
          data-autofocus
        />

        {nothing ? (
          <Text size="sm" data-testid="tree-move-none">
            {t('treeEditor.move.none')}
          </Text>
        ) : (
          <Radio.Group
            value={choice}
            onChange={setChoice}
            label={t('treeEditor.move.groupLabel')}
            data-testid="tree-move-options"
          >
            <Stack gap="xs" mt="xs" mah="16rem" style={{ overflowY: 'auto' }}>
              {targets.rootAllowed && query.trim() === '' && (
                <Radio
                  value={RootValue}
                  label={`${t('treeEditor.move.root')}${currentValue === RootValue ? ` ${t('treeEditor.move.current')}` : ''}`}
                  disabled={currentValue === RootValue}
                />
              )}
              {targets.items.map((item) => {
                const value = String(item.node.id);

                return (
                  <Radio
                    key={value}
                    value={value}
                    label={`${item.path}${value === currentValue ? ` ${t('treeEditor.move.current')}` : ''}`}
                    disabled={value === currentValue}
                  />
                );
              })}
            </Stack>
          </Radio.Group>
        )}

        {targets.total > targets.items.length && (
          <Text size="xs" c="dimmed">
            {t('treeEditor.move.limited', { count: targets.items.length })}
          </Text>
        )}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose} data-testid="tree-move-cancel">
            {t('common.cancel')}
          </Button>
          <Button onClick={confirm} disabled={choice === null} loading={busy} data-testid="tree-move-confirm">
            {t('treeEditor.move.confirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
