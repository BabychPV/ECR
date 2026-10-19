import { Suspense, lazy, useState, type JSX } from 'react';
import { Badge, Button, Group, Skeleton, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { TemplateTable } from '@/features/templates/ctor/ctorModel';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import '@/features/permits/table/permitDefTable.css';
import './bindings.css';
import { deletePermitBinding, listPermitBindings } from '../api/bindingsApi';
import type { PermitBinding } from '../api/bindingsTypes';
import { getPermitType } from '../api/typesApi';
import { pickName } from '../typeEditor/dialogs/names';
import { bindingsKey } from './bindingsKeys';

// ⚠ Обидва — окремі чанки (бюджет маршруту `D-132`): майстер і перегляд потрібні лише після дії користувача.
const BindingWizard = lazy(async () => ({ default: (await import('./BindingWizard')).BindingWizard }));
const BindingPreview = lazy(async () => ({ default: (await import('./BindingPreview')).BindingPreview }));

export interface PermitBindingPanelProps {
  readonly templateVersionId: number;
  readonly table: TemplateTable;
  /** Право `Template.Edit` і незаморожена версія — те саме, що відкриває решту правок структури. */
  readonly canEdit: boolean;
}

function roleText(role: string): string {
  switch (role) {
    case 'RowKey':
      return t('permits.sheetBinding.role.rowKey');
    case 'Limit':
      return t('permits.sheetBinding.role.limit');
    default:
      return t('permits.sheetBinding.role.info');
  }
}

function roleNote(role: string): string {
  switch (role) {
    case 'RowKey':
      return t('permits.sheetBinding.map.noteRowKey');
    case 'Limit':
      return t('permits.sheetBinding.map.noteLimit');
    default:
      return t('permits.sheetBinding.map.noteInfo');
  }
}

/**
 * Вкладка «Permit binding» таблиці аркуша в конструкторі версії шаблону (Permits, PM-11; макет: екран
 * `Sheet binding` — стрілка «аркуш › дозвіл», зіставлення стовпців, перегляд очима користувача).
 *
 * ⚠ Прив'язка однієї таблиці: одна таблиця аркуша читає один дозвіл (сервер: `409 ECR-PRM-4101`), тож вкладка
 * показує або порожній стан із «Bind to a permit…», або наявну прив'язку з діями «Edit» і «Remove binding».
 * ⛔ Права: без `canEdit` (немає `Template.Edit` або версія опублікована) кнопок запису немає зовсім.
 */
export function PermitBindingPanel({ templateVersionId, table, canEdit }: PermitBindingPanelProps): JSX.Element {
  const queryClient = useQueryClient();
  const [wizardOpen, setWizardOpen] = useState(false);
  const [removing, setRemoving] = useState(false);

  const list = useQuery({
    queryKey: bindingsKey(templateVersionId),
    queryFn: () => listPermitBindings(templateVersionId),
  });

  const binding: PermitBinding | undefined = list.data?.items.find((item) => item.sheetTableDefId === table.id);
  const frozen = list.data?.isEditable === false;
  const mayEdit = canEdit && !frozen;

  const type = useQuery({
    queryKey: ['permit-type', binding?.permitTypeCode ?? ''],
    queryFn: () => getPermitType(binding?.permitTypeCode ?? ''),
    enabled: binding !== undefined,
  });

  const remove = useMutation({
    mutationFn: (code: string) => deletePermitBinding(templateVersionId, code),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: bindingsKey(templateVersionId) });
      setRemoving(false);
      showDone(t('permits.sheetBinding.removed'));
    },
    onError: (error: unknown) => {
      setRemoving(false);
      showApiError(error);
    },
  });

  if (list.isPending) return <Skeleton height={120} radius="sm" />;
  if (list.error !== null) return <ErrorAlert error={list.error} onRetry={() => void list.refetch()} />;

  const permitTable = type.data?.tables.find((entry) => entry.code === binding?.permitTableCode);
  const permitColumnName = (code: string): string => {
    const column = permitTable?.columns.find((entry) => entry.code === code);

    return column === undefined ? code : pickName(column.nameL10n) || code;
  };
  const sheetColumnName = (code: string): string => {
    const column = table.columns.find((entry) => entry.code === code);

    return column === undefined ? code : localized(column.headerL10n) || code;
  };

  return (
    <div data-testid="permit-binding-panel">
      {frozen && canEdit ? <Banner tone="warning" text={t('permits.sheetBinding.frozen')} testId="binding-frozen" /> : null}

      {binding === undefined ? (
        <>
          <Text size="sm" c="dimmed" mb="sm" data-testid="binding-empty">
            {t('permits.sheetBinding.empty')}
          </Text>
          {mayEdit ? (
            <Button variant="default" onClick={() => setWizardOpen(true)}>
              {t('permits.sheetBinding.bind')}
            </Button>
          ) : null}
        </>
      ) : (
        <>
          <Group justify="space-between" align="flex-start" mb="sm" wrap="nowrap">
            <div className="pm-arrow" data-testid="binding-summary">
              <code>{localized(table.nameL10n) || table.code}</code>
              <span aria-hidden="true">←</span>
              <code>{`${type.data === undefined ? binding.permitTypeCode : pickName(type.data.nameL10n)} › ${
                permitTable === undefined ? binding.permitTableCode : pickName(permitTable.nameL10n)
              }`}</code>
              <Badge variant="light">
                {binding.mode === 'Rows' ? t('permits.sheetBinding.mode.rows') : t('permits.sheetBinding.mode.formulas')}
              </Badge>
              <Badge variant="outline">
                {binding.selectionRule === 'FromHeaderLookup'
                  ? t('permits.sheetBinding.rule.fromHeader')
                  : t('permits.sheetBinding.rule.byFacility')}
              </Badge>
              {binding.isActive ? null : <Badge color="gray">{t('permits.sheetBinding.inactive')}</Badge>}
            </div>
            {mayEdit ? (
              <Group gap="xs" wrap="nowrap">
                <Button size="xs" variant="default" onClick={() => setWizardOpen(true)}>
                  {t('permits.sheetBinding.edit')}
                </Button>
                <Button size="xs" variant="subtle" color="statusError" onClick={() => setRemoving(true)}>
                  {t('permits.sheetBinding.remove')}
                </Button>
              </Group>
            ) : null}
          </Group>

          {binding.mode === 'Formulas' ? (
            <Banner tone="info" text={t('permits.sheetBinding.formulasOnly')} testId="binding-formulas-only" />
          ) : (
            <section aria-labelledby="pm-bind-map-h">
              <h3 id="pm-bind-map-h">{t('permits.sheetBinding.map.title')}</h3>
              <div className="pm-tw" tabIndex={0} role="region" aria-label={t('permits.sheetBinding.map.title')}>
                <table className="pm-t">
                  <thead>
                    <tr className="pm-cols">
                      <th scope="col">{t('permits.sheetBinding.map.sheetColumn')}</th>
                      <th scope="col">{t('permits.sheetBinding.map.permitColumn')}</th>
                      <th scope="col">{t('permits.sheetBinding.map.role')}</th>
                      <th scope="col">{t('permits.sheetBinding.map.onSheet')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {binding.columns.map((column) => (
                      <tr key={column.permitFieldCode}>
                        <td>{sheetColumnName(column.sheetColumnCode)}</td>
                        <td>{permitColumnName(column.permitFieldCode)}</td>
                        <td>{roleText(column.role)}</td>
                        <td>{roleNote(column.role)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}

          <Suspense fallback={<Skeleton height={120} radius="sm" mt="md" />}>
            <BindingPreview templateVersionId={templateVersionId} binding={binding} />
          </Suspense>
        </>
      )}

      {wizardOpen ? (
        <Suspense fallback={null}>
          <BindingWizard
            opened
            templateVersionId={templateVersionId}
            table={table}
            existing={binding ?? null}
            onClose={() => setWizardOpen(false)}
          />
        </Suspense>
      ) : null}

      <ConfirmModal
        opened={removing && binding !== undefined}
        title={t('permits.sheetBinding.removeTitle', { name: localized(table.nameL10n) || table.code })}
        text={t('permits.sheetBinding.removeText')}
        consequences={[{ text: t('permits.sheetBinding.removeBreaking') }, { text: t('permits.sheetBinding.removeNote'), note: true }]}
        verb={t('permits.sheetBinding.removeVerb')}
        isPending={remove.isPending}
        onConfirm={() => {
          if (binding !== undefined) remove.mutate(binding.code);
        }}
        onClose={() => setRemoving(false)}
      />
    </div>
  );
}
