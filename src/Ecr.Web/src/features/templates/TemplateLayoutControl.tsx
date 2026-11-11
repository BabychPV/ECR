import type { JSX } from 'react';
import { Badge, Group, Select, Text, Tooltip } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { showApiError, showDone } from '@/shared/ui/notify';

/** Розкладка версії шаблону (D-474): копія на місяць або рік із місяцями в колонках. */
export type TemplateLayout = components['schemas']['TemplateLayout'];

type SetTemplateLayoutRequest = components['schemas']['SetTemplateLayoutRequest'];
type TemplateLayoutDto = components['schemas']['TemplateLayoutDto'];

/** Порядок варіантів у списку: наявна поведінка першою. */
const Layouts: readonly TemplateLayout[] = ['PerMonthCopies', 'YearWithMonthColumns'];

/**
 * ✎ D-474: `PUT /template-versions/{id}/layout` `{ layout }`, право `Template.Edit`.
 *
 * ⛔ Лише чернетка без документів: інакше сервер відповідає `409 ECR-SCHM-0409`
 * (`err.ECR-SCHM-0409.templateLayoutBreaking`) — текст відмови показує загальний шлях `showApiError`.
 */
export function setTemplateLayout(templateVersionId: number, layout: TemplateLayout): Promise<TemplateLayoutDto> {
  return apiFetch<TemplateLayoutDto>(`/api/v1/template-versions/${String(templateVersionId)}/layout`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ layout } satisfies SetTemplateLayoutRequest),
  });
}

/**
 * Підпис розкладки — ЛІТЕРАЛАМИ ключів (сторож каталогу `EndpointCoverageTests` читає лише літерали).
 */
export function templateLayoutLabel(layout: TemplateLayout): string {
  return layout === 'YearWithMonthColumns'
    ? t('template.layout.yearWithMonthColumns')
    : t('template.layout.perMonthCopies');
}

/**
 * «Layout» версії шаблону в конструкторі (D-474, п. 2).
 *
 * ⚠ Чернетка з правом `Template.Edit` (`editable`) — список із двох варіантів, запис одразу при виборі (як решта
 * властивостей версії, окремої кнопки «Зберегти» немає). Опублікована / виведена з обігу версія — значок лише для
 * читання з підказкою (розкладка змінюється лише новою версією).
 *
 * ⚠ Після запису перечитуються структура версії (`templates.version`) і обидва переліки версій
 * (`allVersionsOf`, `allVersionsBatch`): рядок переліку теж несе `layout`. Другого ключа кешу немає.
 */
export function TemplateLayoutControl({
  templateVersionId,
  layout,
  editable,
  onSaved,
}: {
  readonly templateVersionId: number;
  readonly layout: TemplateLayout;
  readonly editable: boolean;
  readonly onSaved?: () => void;
}): JSX.Element {
  const queryClient = useQueryClient();

  const save = useMutation({
    mutationFn: (next: TemplateLayout) => setTemplateLayout(templateVersionId, next),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.templates.version(templateVersionId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.templates.allVersionsOf() }),
        queryClient.invalidateQueries({ queryKey: queryKeys.templates.allVersionsBatch() }),
      ]);
      showDone(t('template.layout.saved'));
      onSaved?.();
    },
    onError: showApiError,
  });

  if (!editable) {
    return (
      <Group gap="xs" mb="sm" data-template-layout="readonly">
        <Text size="sm" c="dimmed">
          {t('template.layout.label')}
        </Text>
        <Tooltip label={t('template.layout.hint')} multiline maw={320} withArrow>
          <Badge variant="light" color="gray" tabIndex={0} data-template-layout-value={layout}>
            {templateLayoutLabel(layout)}
          </Badge>
        </Tooltip>
      </Group>
    );
  }

  // ⚠ Поки запис іде — показуємо щойно вибране; відмова повертає значення сервера (`layout` не змінився).
  const shown = save.isPending ? (save.variables ?? layout) : layout;

  return (
    <Select
      mb="sm"
      size="xs"
      miw={280}
      maw={420}
      label={t('template.layout.label')}
      description={t('template.layout.hint')}
      data={Layouts.map((value) => ({ value, label: templateLayoutLabel(value) }))}
      value={shown}
      allowDeselect={false}
      disabled={save.isPending}
      onChange={(value) => {
        if (value === null || value === layout) return;
        save.mutate(value as TemplateLayout);
      }}
      data-template-layout="editable"
    />
  );
}
