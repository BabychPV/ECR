import { useState, type JSX } from 'react';
import { Badge, Group, Select, TextInput } from '@mantine/core';
import { useNavigate } from 'react-router-dom';
import { EcrApiError } from '@/api/client';
import { language, t } from '@/shared/i18n';
import { KeyValue } from '@/shared/ui/KeyValue';
import { showDone } from '@/shared/ui/notify';
import { problemText } from '@/shared/ui/problemText';
import { Wizard, type WizardStep } from '@/shared/ui/Wizard';
import type { CreatePermitTypeRequest, PermitTypeListItem } from '../api/types';
import { typeEditorPath } from '../shell/permitPaths';
import { checkNewTypeName, deriveTypeCode, isValidTypeCode, type NewTypeProblem } from './typeCode';
import { PermitCategories, categoryLabel, typeName } from './typesListModel';
import { useCreatePermitType } from './useTypes';

interface NewTypeData {
  readonly name: string;
  readonly category: string;
  /** `blank` або код типу, чий склад копіюється. */
  readonly start: string;
}

const Blank = 'blank';
const NameField = 'permit-type-name';
const InitialData: NewTypeData = { name: '', category: 'Air', start: Blank };

export interface NewTypeWizardProps {
  readonly opened: boolean;
  /** Типи, що вже є: для перевірки дубля назви й варіантів «Start from». */
  readonly types: readonly PermitTypeListItem[];
  readonly onClose: () => void;
}

/** Підписи відмов за випадком: [підпис під полем, речення в банері кроку]. */
function problemTexts(problem: NewTypeProblem, code: string): readonly [string, string] {
  switch (problem) {
    case 'required':
      return [t('permits.types.wizard.nameRequired'), t('permits.types.wizard.nameRequiredText')];
    case 'nameTaken':
      return [t('permits.types.wizard.nameTaken'), t('permits.types.wizard.nameTakenText')];
    case 'codeInvalid':
      return [t('permits.types.wizard.codeInvalid'), t('permits.types.wizard.codeInvalidText')];
    case 'codeTaken':
      return [t('permits.types.wizard.codeTaken'), t('permits.types.wizard.codeTakenText', { code })];
  }
}

/** Шапка перміта однакова в усіх типів (макет, підсумок «Fields»). */
function HeaderFields(): JSX.Element {
  const names = [
    t('permits.types.wizard.fieldNumber'),
    t('permits.types.wizard.fieldLocation'),
    t('permits.types.wizard.fieldIssued'),
    t('permits.types.wizard.fieldValidFrom'),
    t('permits.types.wizard.fieldValidTo'),
    t('permits.types.wizard.fieldActualEnd'),
  ];

  return (
    <Group gap="xs" wrap="wrap">
      {names.map((name) => (
        <Badge key={name} variant="default" tt="none">
          {name}
        </Badge>
      ))}
    </Group>
  );
}

/**
 * Майстер «New permit type» (PM-08a; макет `screens-permits.js`, діалог `new-type`): один крок
 * «Basics» (назва, категорія, «Start from» — порожній тип чи копія наявного), підсумок і «Create type».
 * Успіх — тост і перехід у редактор типу; `409 ECR-PRM-4091` (код зайнятий) повертає на поле назви.
 *
 * ⚠ Код береться з англійської назви (`deriveTypeCode`) і показується лише підказкою під підписом:
 * окремого поля коду макет не має, а змінити його після заведення не можна.
 */
export function NewTypeWizard({ opened, types, onClose }: NewTypeWizardProps): JSX.Element {
  const navigate = useNavigate();
  const create = useCreatePermitType();
  const lang = language();
  const [nameError, setNameError] = useState<string | null>(null);

  const steps: readonly WizardStep<NewTypeData>[] = [
    {
      id: 'basics',
      label: t('permits.types.wizard.basics'),
      hint: t('permits.types.wizard.basicsHint'),
      validate: (data) => {
        const problem = checkNewTypeName(data.name, types);
        if (problem === null) {
          setNameError(null);

          return null;
        }
        const [field, banner] = problemTexts(problem, deriveTypeCode(data.name.trim()));
        setNameError(field);

        return { message: banner, focus: `input[name="${NameField}"]` };
      },
      render: ({ data, update }) => {
        const code = deriveTypeCode(data.name.trim());

        return (
          <>
            <TextInput
              name={NameField}
              label={t('permits.types.wizard.name')}
              placeholder={t('permits.types.wizard.namePlaceholder')}
              description={isValidTypeCode(code) ? t('permits.types.wizard.code', { code }) : undefined}
              required
              value={data.name}
              error={nameError}
              onChange={(event) => {
                const value = event.currentTarget.value;
                setNameError(null);
                update({ name: value });
              }}
            />
            <Select
              label={t('permits.types.wizard.category')}
              description={t('permits.types.wizard.categoryHint')}
              data={PermitCategories.map((value) => ({ value, label: categoryLabel(value) }))}
              value={data.category}
              allowDeselect={false}
              onChange={(value) => update({ category: value ?? 'Air' })}
            />
            <Select
              label={t('permits.types.wizard.start')}
              data={[
                { value: Blank, label: t('permits.types.wizard.startBlank') },
                ...types.map((item) => ({
                  value: item.code,
                  label: t('permits.types.wizard.startCopy', { name: typeName(item, lang) }),
                })),
              ]}
              value={data.start}
              allowDeselect={false}
              onChange={(value) => update({ start: value ?? Blank })}
            />
          </>
        );
      },
    },
  ];

  return (
    <Wizard<NewTypeData>
      opened={opened}
      title={t('permits.types.wizard.title')}
      initialData={InitialData}
      steps={steps}
      labels={{ back: t('wizard.back'), next: t('wizard.next'), review: t('wizard.review') }}
      reviewText={t('permits.types.wizard.review')}
      applyLabel={t('permits.types.wizard.apply')}
      isApplying={create.isPending}
      summary={(data) => (
        <KeyValue
          items={[
            { label: t('permits.types.wizard.sumName'), value: data.name.trim() },
            { label: t('permits.types.wizard.sumCategory'), value: categoryLabel(data.category) },
            { label: t('permits.types.wizard.sumFields'), value: <HeaderFields /> },
            { label: t('permits.types.wizard.sumStarts'), value: t('permits.types.wizard.sumStartsValue') },
          ]}
        />
      )}
      onApply={(data, api) => {
        const code = deriveTypeCode(data.name.trim());
        const body: CreatePermitTypeRequest = {
          code,
          nameL10n: { en: data.name.trim() },
          category: data.category,
          ...(data.start === Blank ? {} : { copyFromCode: data.start }),
        };

        create.mutate(body, {
          onSuccess: (created) => {
            api.close();
            showDone(t('permits.types.created'));
            void navigate(typeEditorPath(created.code.length > 0 ? created.code : code));
          },
          onError: (error) => {
            if (error instanceof EcrApiError && error.problem.errorCode === 'ECR-PRM-4091') {
              // Код зайнятий (гонитва з іншою сесією або чужа назва, що дає той самий код): назад до назви.
              const [field, banner] = problemTexts('codeTaken', code);
              setNameError(field);
              api.fail(banner);

              return;
            }
            const shown = problemText(error);
            api.fail(shown.detail === null ? shown.title : `${shown.title}: ${shown.detail}`);
          },
        });
      }}
      onClose={onClose}
      // ⚠ Нічого не створено до «Create type», а введене відновлюється за кілька секунд.
      onExitUnsaved={() => true}
    />
  );
}
