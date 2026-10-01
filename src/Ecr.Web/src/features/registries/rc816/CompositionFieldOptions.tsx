import type { JSX } from 'react';
import { Checkbox, NativeSelect, Stack, Text } from '@mantine/core';
import type { RegistryDefinitionDto } from '@/api/types';
import { t } from '@/shared/i18n';
import {
  ParentDeletePolicies,
  compositionIssues,
  isComposition,
  type CompositionDraft,
  type CompositionIssue,
  type ParentDeletePolicy,
} from './composition';

/** Текст порушення — літералом, щоб сторож каталогу бачив кожен ключ. */
function issueText(issue: CompositionIssue): string {
  switch (issue) {
    case 'registries.rc816.issueMoreThanOne':
      return t('registries.rc816.issueMoreThanOne');
    case 'registries.rc816.issueTargetSelf':
      return t('registries.rc816.issueTargetSelf');
    case 'registries.rc816.issueTemporal':
      return t('registries.rc816.issueTemporal');
    case 'registries.rc816.issueNoTarget':
      return t('registries.rc816.issueNoTarget');
  }
}

/** Нове поле конструктора в тій мірі, в якій його стосується композиція. */
type Draft = CompositionDraft & { readonly dataType: string; readonly lookupRegistryDefId: number | null };

/**
 * Перемикач «Частина батька» і політика видалення батька для НОВОГО поля `Lookup`
 * (`ФВ-8.16`, FEATURE-REGISTRY-TABLES §8.3: «композиція задається під час створення поля»).
 *
 * ⛔ Лише для нового поля: відношення наявного сервер не змінює
 * (`err.ECR-REG-0422.relationKindImmutable`) — наявні записи раптом стали б частинами, невидимими
 * без батька. Тому в рядку наявного поля цього контролю немає зовсім.
 *
 * ⚠ Порушення (друге поле композиції, ціль — сам довідник, темпоральний довідник) показуються
 * словами до збереження; збереження однаково вирішує сервер (`RegistryCompositionRules.Validate`).
 */
export function CompositionFieldOptions<T extends Draft>({
  definition,
  draft,
  otherNew,
  canEdit,
  onChange,
}: {
  readonly definition: Pick<RegistryDefinitionDto, 'id' | 'isTemporal' | 'relations'>;
  readonly draft: T;
  readonly otherNew: readonly Draft[];
  readonly canEdit: boolean;
  readonly onChange: (draft: T) => void;
}): JSX.Element | null {
  if (draft.dataType !== 'Lookup') return null;

  const composed = isComposition(draft);
  const issues = compositionIssues(definition, draft, otherNew);

  return (
    <Stack gap="xs" mt="xs" data-rc816-composition>
      <Checkbox
        size="xs"
        label={t('registries.rc816.partOfParent')}
        description={t('registries.rc816.partOfParentHint')}
        disabled={!canEdit}
        checked={composed}
        onChange={(event) =>
          onChange(
            event.currentTarget.checked
              ? { ...draft, relationKind: 'Composition', onParentDelete: draft.onParentDelete ?? 'Restrict' }
              : { ...draft, relationKind: null, onParentDelete: null },
          )
        }
      />
      {composed && (
        <NativeSelect
          size="xs"
          label={t('registries.rc816.onParentDelete')}
          disabled={!canEdit}
          value={draft.onParentDelete ?? 'Restrict'}
          data={ParentDeletePolicies.map((policy) => ({
            value: policy,
            label: policy === 'Cascade' ? t('registries.rc816.policyCascade') : t('registries.rc816.policyRestrict'),
          }))}
          onChange={(event) =>
            onChange({ ...draft, onParentDelete: event.currentTarget.value as ParentDeletePolicy })
          }
        />
      )}
      {issues.map((issue) => (
        <Text key={issue} size="xs" c="statusError" role="alert">
          {issueText(issue)}
        </Text>
      ))}
    </Stack>
  );
}
