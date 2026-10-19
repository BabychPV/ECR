import { Suspense, lazy, useState, type JSX } from 'react';
import { Button, Group, Modal, Text, TextInput } from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { formatDate } from '@/shared/format';
import { language, t } from '@/shared/i18n';
import { KeyValue } from '@/shared/ui/KeyValue';
import { showDone } from '@/shared/ui/notify';
import { problemText } from '@/shared/ui/problemText';
import { Wizard, type WizardStep } from '@/shared/ui/Wizard';
import type { PermitTypeListItem } from '../api/types';
import { typeName, tablesText } from '../types/typesListModel';
import { useAllPermitTypes } from '../types/useTypes';
import {
  checkDetails,
  checkType,
  initialData,
  publishedTypes,
  toRequest,
  type NewPermitData,
  type NewPermitProblem,
} from './newPermitModel';
import { permitHref } from './permitsListModel';
import { useCreatePermit } from './usePermits';
import './newPermit.css';

/*
 * ⚠ Поле дати - за `import()`: `@mantine/dates` тягне `dayjs`, а потрібне воно лише на кроці «Details» (D-132).
 * Рідне `type="date"` заборонено (D15-09): воно бере формат з ОС.
 */
const DateOnlyInput = lazy(async () => ({
  default: (await import('@/shared/dates/DateInputWithStyles')).DateOnlyInput,
}));

const NumberField = 'permit-number';

export interface NewPermitWizardProps {
  readonly opened: boolean;
  readonly onClose: () => void;
}

/** [підпис під полем, речення в банері кроку] за випадком. */
function problemTexts(problem: NewPermitProblem): readonly [string | null, string] {
  switch (problem) {
    case 'typeRequired':
      return [null, t('permits.list.wizard.typeRequired')];
    case 'numberRequired':
      return [t('permits.list.wizard.numberRequired'), t('permits.list.wizard.numberRequiredText')];
    case 'datesRequired':
      return [null, t('permits.list.wizard.datesRequired')];
    case 'validityOrder':
      return [null, t('permits.list.wizard.validityOrder')];
  }
}

function TypePick({
  types,
  value,
  onChange,
}: {
  readonly types: readonly PermitTypeListItem[];
  readonly value: string;
  readonly onChange: (code: string) => void;
}): JSX.Element {
  const lang = language();

  return (
    <fieldset className="pm-typepick" data-testid="permit-type-pick">
      <legend>{t('permits.list.wizard.type')}</legend>
      {types.map((item) => {
        const ready = item.status === 'Published';
        const id = `permit-type-${item.code}`;

        return (
          <label key={item.code} htmlFor={id} className="pm-typepick-item" data-checked={value === item.code} data-disabled={!ready}>
            <input
              id={id}
              type="radio"
              name="permit-type"
              value={item.code}
              checked={value === item.code}
              disabled={!ready}
              onChange={() => onChange(item.code)}
            />
            <span>
              <span>{typeName(item, lang)}</span>
              <span className="pm-typepick-hint">
                {ready ? tablesText(item, lang) : t('permits.list.wizard.notPublished')}
              </span>
            </span>
          </label>
        );
      })}
    </fieldset>
  );
}

/** Сам майстер; змонтований лише після того, як перелік типів приїхав (початковий вибір залежить від нього). */
function WizardBody({
  opened,
  types,
  onClose,
}: NewPermitWizardProps & { readonly types: readonly PermitTypeListItem[] }): JSX.Element {
  const navigate = useNavigate();
  const create = useCreatePermit();
  const applying = usePendingLoading(create.isPending);
  const lang = language();
  const [numberError, setNumberError] = useState<string | null>(null);
  const published = publishedTypes(types);

  const steps: readonly WizardStep<NewPermitData>[] = [
    {
      id: 'kind',
      label: t('permits.list.wizard.stepType'),
      hint: t('permits.list.wizard.typeHint'),
      validate: (data) => {
        const problem = checkType(data, types);

        return problem === null ? null : { message: problemTexts(problem)[1], focus: 'input[name="permit-type"]' };
      },
      render: ({ data, update }) =>
        published.length === 0 ? (
          <Text size="sm">{t('permits.list.wizard.noTypes')}</Text>
        ) : (
          <TypePick types={types} value={data.type} onChange={(code) => update({ type: code })} />
        ),
    },
    {
      id: 'head',
      label: t('permits.list.wizard.stepDetails'),
      hint: t('permits.list.wizard.detailsHint'),
      validate: (data) => {
        const problem = checkDetails(data);
        if (problem === null) {
          setNumberError(null);

          return null;
        }
        const [field, banner] = problemTexts(problem);
        setNumberError(problem === 'numberRequired' ? field : null);

        return {
          message: banner,
          ...(problem === 'numberRequired' ? { focus: `input[name="${NumberField}"]` } : {}),
        };
      },
      render: ({ data, update }) => (
        <div className="pm-newpermit-grid">
          <TextInput
            name={NumberField}
            label={t('permits.list.wizard.number')}
            required
            value={data.number}
            error={numberError}
            onChange={(event) => {
              const value = event.currentTarget.value;
              setNumberError(null);
              update({ number: value });
            }}
          />
          <TextInput
            name="permit-location"
            label={t('permits.list.wizard.location')}
            value={data.location}
            onChange={(event) => update({ location: event.currentTarget.value })}
          />
          <Suspense fallback={null}>
            <DateOnlyInput
              label={t('permits.list.wizard.validFrom')}
              clearable={false}
              value={data.validFrom}
              onChange={(value) => update({ validFrom: value })}
            />
            <DateOnlyInput
              label={t('permits.list.wizard.validTo')}
              description={t('permits.list.wizard.validToHint')}
              clearable={false}
              value={data.validTo}
              onChange={(value) => update({ validTo: value })}
            />
          </Suspense>
        </div>
      ),
    },
  ];

  return (
    <Wizard<NewPermitData>
      opened={opened}
      title={t('permits.list.wizard.title')}
      initialData={initialData(new Date(), types)}
      steps={steps}
      labels={{ back: t('wizard.back'), next: t('wizard.next'), review: t('wizard.review') }}
      reviewText={t('permits.list.wizard.review')}
      applyLabel={t('permits.list.wizard.apply')}
      isApplying={applying}
      summary={(data) => {
        const picked = types.find((item) => item.code === data.type);

        return (
          <KeyValue
            items={[
              { label: t('permits.list.wizard.sumType'), value: picked === undefined ? data.type : typeName(picked, lang) },
              { label: t('permits.list.wizard.sumNumber'), value: data.number.trim() },
              { label: t('permits.list.wizard.sumLocation'), value: data.location.trim() },
              { label: t('permits.list.wizard.sumValid'), value: `${formatDate(data.validFrom)} - ${formatDate(data.validTo)}` },
              { label: t('permits.list.wizard.sumStarts'), value: t('permits.list.wizard.sumStartsValue') },
            ]}
          />
        );
      }}
      onApply={(data, api) => {
        if (create.isPending) return;
        create.mutate(toRequest(data), {
          onSuccess: (created) => {
            api.close();
            showDone(t('permits.list.done.created'));
            void navigate(permitHref(created.id));
          },
          onError: (error) => {
            // Відмова сервера (`4095` номер зайнятий, `0422`) — текстом каталогу у банері кроку; майстер лишається.
            const shown = problemText(error);
            api.fail(shown.detail === null ? shown.title : `${shown.title}: ${shown.detail}`);
          },
        });
      }}
      onClose={onClose}
      // ⚠ Нічого не створено до «Create permit», а введене відновлюється за хвилину.
      onExitUnsaved={() => true}
    />
  );
}

/**
 * Майстер «New permit» (PM-07; макет `new-permit`): крок «Type» (опубліковані типи списком, неопубліковані
 * недоступні з поясненням), крок «Details» (номер, розташування, чинність), підсумок і «Create permit».
 * Успіх — тост і перехід на сторінку дозволу-чернетки.
 */
export function NewPermitWizard({ opened, onClose }: NewPermitWizardProps): JSX.Element | null {
  const types = useAllPermitTypes();

  if (types.data !== undefined) return <WizardBody opened={opened} types={types.data} onClose={onClose} />;
  if (!opened) return null;

  return (
    <Modal opened onClose={onClose} title={t('permits.list.wizard.title')}>
      <Text size="sm" role={types.isError ? 'alert' : 'status'}>
        {types.isError ? problemText(types.error).title : t('permits.list.wizard.loading')}
      </Text>
      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={onClose} data-autofocus>
          {t('common.cancel')}
        </Button>
        {types.isError ? <Button onClick={() => void types.refetch()}>{t('common.retry')}</Button> : null}
      </Group>
    </Modal>
  );
}
