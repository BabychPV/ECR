import { useState, type JSX } from 'react';
import { ActionIcon, Button, Group, Modal, Paper, Stack, Text } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { TemplateHeaderGroupDto } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import { showDone } from '@/shared/ui/notify';
import {
  MaxGroupDepth,
  addGroup,
  draftsOf,
  flatten,
  isValid,
  moveGroup,
  relabel,
  removeGroup,
  siblingsOf,
  toRequest,
  type GroupDraft,
} from './headerGroupTree';
import { replaceHeaderGroups } from './headerGroupsApi';

/**
 * Діалог «Групи колонок»: дерево до 3 рівнів, назви мовами каталогу, порядок серед сусідів.
 * Зберігається ВСЕ дерево одним PUT — презентаційна операція, доступна й на опублікованій версії.
 *
 * ⚠ Монтується лише відкритим (чернетка береться зі структури при монтуванні), тож повторне
 * відкриття не показує незбережене введення попередньої спроби.
 * ⛔ Помилка сервера (зокрема `ECR-TMPL-4229`, неоднакові сусіди) — у самому діалозі, текстом із каталогу.
 */
export function ColumnGroupsDialog({
  templateVersionId,
  tableId,
  groups,
  onClose,
}: {
  readonly templateVersionId: number;
  readonly tableId: number;
  readonly groups: readonly TemplateHeaderGroupDto[] | undefined;
  readonly onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [drafts, setDrafts] = useState<GroupDraft[]>(() => draftsOf(groups));

  const save = useMutation({
    // Відмову (зокрема ECR-TMPL-4229) показує сам діалог — без другого тосту глобальної сітки.
    meta: { handled: true },
    mutationFn: () => replaceHeaderGroups(templateVersionId, tableId, toRequest(drafts)),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(templateVersionId) });
      showDone(t('colgroups.saved', { revision: result.presentationRevision }));
      onClose();
    },
  });

  const saveLoading = usePendingLoading(save.isPending);
  const rows = flatten(drafts);
  const valid = isValid(drafts);

  return (
    <Modal opened onClose={onClose} title={t('colgroups.title')} size="xl">
      <Stack gap="sm" data-testid="column-groups-dialog">
        <Text size="sm" c="dimmed">
          {t('colgroups.hint')}
        </Text>

        {rows.length === 0 && (
          <Text size="sm" data-testid="column-groups-empty">
            {t('colgroups.empty')}
          </Text>
        )}

        {rows.map(({ group, depth }) => {
          const siblings = siblingsOf(drafts, group.parentCode);
          const index = siblings.findIndex((item) => item.code === group.code);

          return (
            <Paper
              key={group.code}
              withBorder
              p="sm"
              ml={`calc(var(--mantine-spacing-lg) * ${String(depth - 1)})`}
              data-group-code={group.code}
              data-group-depth={depth}
            >
              <LocalizedInput
                label={t('colgroups.label')}
                value={group.label}
                onChange={(next) => setDrafts((current) => relabel(current, group.code, next))}
              />
              <Group gap="xs" mt="xs">
                <ActionIcon
                  variant="default"
                  aria-label={t('colgroups.moveUp')}
                  disabled={index <= 0}
                  onClick={() => setDrafts((current) => moveGroup(current, group.code, -1))}
                >
                  ↑
                </ActionIcon>
                <ActionIcon
                  variant="default"
                  aria-label={t('colgroups.moveDown')}
                  disabled={index < 0 || index >= siblings.length - 1}
                  onClick={() => setDrafts((current) => moveGroup(current, group.code, 1))}
                >
                  ↓
                </ActionIcon>
                <Button
                  size="xs"
                  variant="subtle"
                  disabled={depth >= MaxGroupDepth}
                  title={depth >= MaxGroupDepth ? t('colgroups.maxDepth') : undefined}
                  onClick={() => setDrafts((current) => addGroup(current, group.code))}
                >
                  {t('colgroups.addChild')}
                </Button>
                <Button
                  size="xs"
                  variant="subtle"
                  color="statusError"
                  onClick={() => setDrafts((current) => removeGroup(current, group.code))}
                >
                  {t('colgroups.remove')}
                </Button>
              </Group>
            </Paper>
          );
        })}

        <Group>
          <Button variant="default" onClick={() => setDrafts((current) => addGroup(current, null))}>
            {t('colgroups.add')}
          </Button>
        </Group>

        {!valid && (
          <Text size="sm" c="statusError">
            {t('colgroups.needName')}
          </Text>
        )}

        {save.error !== null && <ErrorAlert error={save.error} />}

        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button disabled={!valid} loading={saveLoading} onClick={() => save.mutate()}>
            {t('colgroups.save')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
