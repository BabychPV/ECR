import { useMemo, useState, type JSX } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getHeaderFields } from '@/features/templates/headerFieldApi';
import type { TemplateTable } from '@/features/templates/ctor/ctorModel';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { KeyValue } from '@/shared/ui/KeyValue';
import { problemText } from '@/shared/ui/problemText';
import { Wizard, type WizardStep } from '@/shared/ui/Wizard/Wizard';
import { showDone } from '@/shared/ui/notify';
import { pickName } from '../typeEditor/dialogs/names';
import { savePermitBinding } from '../api/bindingsApi';
import type { PermitBinding } from '../api/bindingsTypes';
import { listPermitTypes } from '../api/typesApi';
import {
  bindingCodeFor,
  draftOfBinding,
  draftProblem,
  emptyDraft,
  mappingProblem,
  sheetRefOf,
  toRequest,
  type MappingProblem,
} from './bindingModel';
import { bindingsKey } from './bindingsKeys';
import { ColumnsStep, PickStep, SheetStep, SourceStep, type WizardData } from './BindingSteps';

export interface BindingWizardProps {
  readonly opened: boolean;
  readonly templateVersionId: number;
  readonly table: TemplateTable;
  /** Правка наявної прив'язки; `null` — створення. */
  readonly existing: PermitBinding | null;
  readonly onClose: () => void;
}

/** Текст вади зіставлення (ключі літералами). */
function mappingText(problem: MappingProblem): string {
  switch (problem.kind) {
    case 'noColumn':
      return t('permits.sheetBinding.problem.noColumn', { fieldCode: problem.fieldCode });
    case 'duplicateColumn':
      return t('permits.sheetBinding.problem.duplicateColumn', { columnCode: problem.columnCode });
    case 'notReadOnly':
      return t('permits.sheetBinding.problem.notReadOnly', { columnCode: problem.columnCode });
    case 'noRowKey':
      return t('permits.sheetBinding.problem.noRowKey');
    case 'manyLimits':
      return t('permits.sheetBinding.problem.manyLimits');
  }
}

/**
 * Майстер «Bind a sheet table to a permit» (макет `new-binding`): таблиця аркуша → таблиця дозволу й режим →
 * який дозвіл → стовпці → підсумок (`Review` додає `Wizard`).
 *
 * ⚠ Таблицю аркуша не вибирають: майстер відкривають з її вкладки (крок лише показує її). Прив'язка належить
 * ЧЕРНЕТЦІ версії: опублікована версія заморожена, тож майстер у ній не відкривається (хост перевіряє).
 * ⛔ Остаточне слово — за сервером (`422 ECR-PRM-0422` з `messageKey`); клієнтські перевірки лише підказують.
 */
export function BindingWizard({ opened, templateVersionId, table, existing, onClose }: BindingWizardProps): JSX.Element {
  const queryClient = useQueryClient();
  const [discarding, setDiscarding] = useState(false);
  const sheet = useMemo(() => sheetRefOf(table), [table]);
  const tableName = localized(table.nameL10n) || table.code;

  const types = useQuery({ queryKey: ['permit-types', 'published'], queryFn: () => listPermitTypes({ limit: 500 }), enabled: opened });
  const headerFields = useQuery({
    queryKey: ['template-header-fields', templateVersionId],
    queryFn: () => getHeaderFields(templateVersionId),
    enabled: opened,
  });

  const publishedTypes = (types.data?.items ?? []).filter((item) => item.version > 0);

  const save = useMutation({
    mutationFn: (data: WizardData) =>
      savePermitBinding(templateVersionId, existing?.code ?? bindingCodeFor(table), toRequest(data, sheet)),
  });

  const initial: WizardData = useMemo(() => {
    if (existing === null) return { ...emptyDraft(), mappingKey: '' };

    // mappingKey пустий: крок «Columns» допише невикористані стовпці дозволу (`completeMapping`).
    return { ...draftOfBinding(existing, undefined), mappingKey: '' };
  }, [existing]);

  const steps: readonly WizardStep<WizardData>[] = [
    {
      id: 'sheet',
      label: t('permits.sheetBinding.wizard.stepSheet'),
      hint: t('permits.sheetBinding.wizard.stepSheetHint'),
      render: () => <SheetStep sheet={sheet} name={tableName} />,
    },
    {
      id: 'source',
      label: t('permits.sheetBinding.wizard.stepSource'),
      hint: t('permits.sheetBinding.wizard.stepSourceHint'),
      render: ({ data, update }) => (
        <SourceStep data={data} update={update} types={publishedTypes} locked={existing !== null} />
      ),
      validate: (data) => {
        const problem = draftProblem(data, sheet);
        if (problem === 'typeRequired') return { message: t('permits.sheetBinding.problem.typeRequired'), focus: '#pm-bind-type' };
        if (problem === 'tableRequired') return { message: t('permits.sheetBinding.problem.tableRequired'), focus: '#pm-bind-table' };
        if (problem === 'rowsNeedDynamic') {
          return { message: t('permits.sheetBinding.problem.rowsNeedDynamic', { tableCode: table.code }) };
        }

        return null;
      },
    },
    {
      id: 'pick',
      label: t('permits.sheetBinding.wizard.stepPick'),
      hint: t('permits.sheetBinding.wizard.stepPickHint'),
      render: ({ data, update }) => <PickStep data={data} update={update} headerFields={headerFields.data ?? []} />,
      validate: (data) =>
        draftProblem(data, sheet) === 'headerFieldRequired'
          ? { message: t('permits.sheetBinding.problem.headerFieldRequired'), focus: '#pm-bind-header' }
          : null,
    },
    {
      id: 'columns',
      label: t('permits.sheetBinding.wizard.stepColumns'),
      hint: t('permits.sheetBinding.wizard.stepColumnsHint'),
      render: ({ data, update }) => <ColumnsStep data={data} update={update} sheet={sheet} />,
      validate: (data) => {
        const problem = mappingProblem(data, sheet);

        return problem === null ? null : { message: mappingText(problem) };
      },
    },
  ];

  return (
    <>
      <Wizard<WizardData>
        opened={opened}
        title={t('permits.sheetBinding.wizard.title', { name: tableName })}
        wide
        initialData={initial}
        steps={steps}
        labels={{
          back: t('permits.editor.dialogs.wizard.back'),
          next: t('permits.editor.dialogs.wizard.next'),
          review: t('permits.editor.dialogs.wizard.review'),
        }}
        reviewText={t('permits.sheetBinding.wizard.reviewText')}
        applyLabel={existing === null ? t('permits.sheetBinding.wizard.create') : t('permits.sheetBinding.wizard.save')}
        isApplying={save.isPending}
        summary={(data) => {
          const type = publishedTypes.find((item) => item.code === data.permitTypeCode);
          const permitTable = type?.tables.find((entry) => entry.code === data.permitTableCode);
          const used = data.mapping.filter((row) => row.role !== 'None').length;

          return (
            <>
              <KeyValue
                wide
                items={[
                  { label: t('permits.sheetBinding.wizard.sumSheet'), value: tableName },
                  {
                    label: t('permits.sheetBinding.wizard.sumReads'),
                    value: `${type === undefined ? data.permitTypeCode : pickName(type.nameL10n)} → ${permitTable === undefined ? data.permitTableCode : pickName(permitTable.nameL10n)}`,
                  },
                  {
                    label: t('permits.sheetBinding.wizard.sumHow'),
                    value: data.mode === 'Rows' ? t('permits.sheetBinding.mode.rows') : t('permits.sheetBinding.mode.formulas'),
                  },
                  {
                    label: t('permits.sheetBinding.wizard.sumPermit'),
                    value:
                      data.selectionRule === 'ByFacilityPeriodEnd'
                        ? t('permits.sheetBinding.rule.byFacility')
                        : t('permits.sheetBinding.rule.fromHeader'),
                  },
                  ...(data.mode === 'Rows'
                    ? [{ label: t('permits.sheetBinding.wizard.sumColumns'), value: String(used) }]
                    : []),
                  { label: t('permits.sheetBinding.wizard.sumCode'), value: existing?.code ?? bindingCodeFor(table), mono: true },
                  { label: t('permits.sheetBinding.wizard.sumEffect'), value: t('permits.sheetBinding.wizard.effect') },
                ]}
              />
              <Banner tone="warning" text={t('permits.sheetBinding.wizard.breakingHint')} testId="binding-breaking-hint" />
            </>
          );
        }}
        onApply={(data, api) => {
          save.mutate(data, {
            onSuccess: async () => {
              await queryClient.invalidateQueries({ queryKey: bindingsKey(templateVersionId) });
              showDone(t('permits.sheetBinding.saved'));
              api.close();
            },
            onError: (error) => {
              const shown = problemText(error);
              api.fail(shown.detail ?? shown.title);
            },
          });
        }}
        onClose={onClose}
        onExitUnsaved={() => {
          setDiscarding(true);

          return false;
        }}
      />

      <ConfirmModal
        opened={discarding}
        title={t('permits.sheetBinding.wizard.discardTitle')}
        text={t('permits.sheetBinding.wizard.discardText')}
        verb={t('permits.sheetBinding.wizard.discardVerb')}
        onConfirm={() => {
          setDiscarding(false);
          onClose();
        }}
        onClose={() => setDiscarding(false)}
      />
    </>
  );
}
