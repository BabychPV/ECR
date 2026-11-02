import { useState, type JSX } from 'react';
import { Badge, Button, Group, Skeleton, Stack, Table, Text, TextInput, VisuallyHidden } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { ResizableModal } from '@/shared/ui/ResizableModal';
import { showApiError, showDone } from '@/shared/ui/notify';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import {
  deleteMethodologyCategory,
  methodologyCategories,
  saveMethodologyCategory,
  type MethodologyCategoryDto,
} from './api';
import { mayDeleteCategory, mayRetireCategory } from './constantCategories';

/** Що правлять у формі: новий код або опис наявного. */
interface CategoryDraft {
  readonly code: string;
  readonly description: string;
  readonly isNew: boolean;
}

const EmptyDraft: CategoryDraft = { code: '', description: '', isNew: true };

/**
 * Перелік категорій констант версії (план CONST-CATEGORY): перегляд, додавання, опис, виведення з ужитку,
 * прибирання незайнятої.
 *
 * ⛔ Перейменування немає і кнопки для нього немає: код — ключ зіставлення, який повертає правило
 * категорії й несуть константи. «Перейменувати» = додати нову, перенести константи, вивести стару
 * (сервер на спробу відповідає `409 categoryRename`).
 *
 * ⚠ Без `AsyncBoundary`: її `<Title order={4}>` у модалці рве `heading-order` (гейти a11y).
 */
export function MethodologyCategoriesDialog({
  methodologyId,
  versionId,
  editable,
  opened,
  onClose,
}: {
  readonly methodologyId: number;
  readonly versionId: number;
  readonly editable: boolean;
  readonly opened: boolean;
  readonly onClose: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<CategoryDraft>(EmptyDraft);
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null);

  const categories = useQuery({
    queryKey: queryKeys.methodologies.categories(versionId),
    queryFn: () => methodologyCategories(methodologyId, versionId),
    enabled: opened,
  });

  const refresh = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: queryKeys.methodologies.categories(versionId) });
  };

  const save = useMutation({
    mutationFn: (input: { code: string; description: string | null; isRetired: boolean | null }) =>
      saveMethodologyCategory(methodologyId, versionId, input.code, {
        description: input.description,
        isRetired: input.isRetired,
        ordinal: null,
      }),
    onSuccess: async () => {
      await refresh();
      setDraft(EmptyDraft);
      showDone(t('methodologies.constCategories.saved'));
    },
    onError: showApiError,
  });

  const remove = useMutation({
    mutationFn: (code: string) => deleteMethodologyCategory(methodologyId, versionId, code),
    onSuccess: async () => {
      await refresh();
      setConfirmDelete(null);
      showDone(t('methodologies.constCategories.deleted'));
    },
    onError: showApiError,
  });

  const saveLoading = usePendingLoading(save.isPending);
  const busy = save.isPending || remove.isPending;
  const code = draft.code.trim();

  const list = [...(categories.data ?? [])].sort(
    (a, b) => a.ordinal - b.ordinal || a.code.localeCompare(b.code),
  );

  const row = (category: MethodologyCategoryDto): JSX.Element => (
    <Table.Tr key={category.code} data-category-code={category.code}>
      <Table.Td>
        <Text size="sm" ff="monospace">
          {category.code}
        </Text>
      </Table.Td>
      <Table.Td>
        <Text size="sm" {...(category.description === null ? { c: 'dimmed' } : {})}>
          {category.description ?? '—'}
        </Text>
      </Table.Td>
      <Table.Td>{category.constantCount}</Table.Td>
      <Table.Td>
        {category.isRetired && (
          <Badge size="xs" variant="outline" color="gray">
            {t('methodologies.constCategories.retired')}
          </Badge>
        )}
      </Table.Td>
      {editable && (
        <Table.Td>
          <Group gap="xs" wrap="nowrap" justify="flex-end">
            <Button
              size="xs"
              variant="subtle"
              disabled={busy}
              aria-label={t('methodologies.constCategories.editAria', { code: category.code })}
              onClick={() =>
                setDraft({ code: category.code, description: category.description ?? '', isNew: false })
              }
            >
              {t('methodologies.editFormula')}
            </Button>
            {category.isRetired ? (
              <Button
                size="xs"
                variant="subtle"
                disabled={busy}
                onClick={() =>
                  save.mutate({ code: category.code, description: category.description, isRetired: false })
                }
              >
                {t('methodologies.constCategories.restore')}
              </Button>
            ) : (
              <Button
                size="xs"
                variant="subtle"
                disabled={busy || !mayRetireCategory(category)}
                title={
                  mayRetireCategory(category)
                    ? undefined
                    : t('methodologies.constCategories.inUseHint', { count: category.constantCount })
                }
                onClick={() =>
                  save.mutate({ code: category.code, description: category.description, isRetired: true })
                }
              >
                {t('methodologies.constCategories.retire')}
              </Button>
            )}
            {confirmDelete === category.code ? (
              <Button
                size="xs"
                color="statusError"
                disabled={busy}
                onClick={() => remove.mutate(category.code)}
              >
                {t('methodologies.constCategories.confirmDelete')}
              </Button>
            ) : (
              <Button
                size="xs"
                variant="subtle"
                color="statusError"
                disabled={busy || !mayDeleteCategory(category)}
                title={
                  mayDeleteCategory(category)
                    ? undefined
                    : t('methodologies.constCategories.inUseHint', { count: category.constantCount })
                }
                onClick={() => setConfirmDelete(category.code)}
              >
                {t('methodologies.deleteFormula')}
              </Button>
            )}
          </Group>
        </Table.Td>
      )}
    </Table.Tr>
  );

  return (
    <ResizableModal
      sizeKey="methodology.categories"
      opened={opened}
      onClose={onClose}
      title={t('methodologies.constCategories.title')}
      size="lg"
    >
      <Stack gap="sm" data-testid="methodology-categories">
        <Text size="sm" c="dimmed">
          {t('methodologies.constCategories.intro')}
        </Text>

        {categories.error !== null ? (
          <ErrorAlert error={categories.error} onRetry={() => void categories.refetch()} />
        ) : categories.isPending ? (
          <Skeleton height={96} />
        ) : list.length === 0 ? (
          <Text size="sm" c="dimmed">
            {t('methodologies.constCategories.empty')}
          </Text>
        ) : (
          <Table.ScrollContainer minWidth={560}>
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('methodologies.code')}</Table.Th>
                <Table.Th>{t('methodologies.constCategories.description')}</Table.Th>
                <Table.Th>{t('methodologies.constCategories.constantCount')}</Table.Th>
                <Table.Th>{t('methodologies.constCategories.state')}</Table.Th>
                {editable && (
                  <Table.Th>
                    <VisuallyHidden>{t('methodologies.constCategories.actions')}</VisuallyHidden>
                  </Table.Th>
                )}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>{list.map(row)}</Table.Tbody>
          </Table>
          </Table.ScrollContainer>
        )}

        {editable ? (
          <Group align="end" gap="sm" wrap="wrap">
            <TextInput
              id="methodology-category-code"
              label={t('methodologies.code')}
              value={draft.code}
              disabled={!draft.isNew}
              maxLength={100}
              onChange={(event) => setDraft({ ...draft, code: event.currentTarget.value })}
            />
            <TextInput
              id="methodology-category-description"
              label={t('methodologies.constCategories.description')}
              value={draft.description}
              maxLength={400}
              style={{ flex: 1, minWidth: 180 }}
              onChange={(event) => setDraft({ ...draft, description: event.currentTarget.value })}
            />
            <Button
              disabled={code.length === 0 || busy}
              loading={saveLoading}
              onClick={() =>
                save.mutate({
                  code,
                  description: draft.description.trim() === '' ? null : draft.description.trim(),
                  isRetired: null,
                })
              }
            >
              {draft.isNew ? t('methodologies.constCategories.add') : t('methodologies.save')}
            </Button>
            {!draft.isNew && (
              <Button variant="default" disabled={busy} onClick={() => setDraft(EmptyDraft)}>
                {t('methodologies.constCategories.cancelEdit')}
              </Button>
            )}
          </Group>
        ) : (
          <Text size="sm" c="dimmed">
            {t('methodologies.constCategories.readOnly')}
          </Text>
        )}
      </Stack>
    </ResizableModal>
  );
}
