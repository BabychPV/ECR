import { useEffect, useRef, useState, type JSX } from 'react';
import { Button, Fieldset, Group, Skeleton, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { invalidateRegistryTree } from '@/features/registries/tree/useRegistryTree';
import { dataTypeLabel } from '@/features/templates/enumLabels';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showApiError, showDone } from '@/shared/ui/notify';
import type { TreeNode } from '@/shared/ui/TreeEditor';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import {
  EmptyNodeForm,
  buildNodeBody,
  formOfDetail,
  hasProblems,
  serverFieldProblem,
  validateNode,
  type NodeDetail,
  type NodeFormProblems,
  type NodeFormState,
  type NodeFormTarget,
  type ServerFieldProblem,
} from './nodeAttributes';

type EntryIdResponse = components['schemas']['RegistryEntryIdResponse'];
type RegistryField = components['schemas']['RegistryFieldDto'];

export interface NodeAttributesFormProps {
  readonly registryCode: string;
  readonly registryId: number;
  readonly fields: readonly RegistryField[];
  readonly target: NodeFormTarget;
  /** Шлях до батька (для підпису «куди додається»); порожній - корінь. */
  readonly parentPath?: readonly TreeNode[];
  /** Без права запису: поля видно, але не змінити, «Save» немає. */
  readonly readOnly: boolean;
  /** Збережено; `id` - запис (для вибору в дереві після створення). */
  readonly onSaved: (id: number) => void;
  readonly onCancel?: (() => void) | undefined;
}

/** Ключ `GET …/entries/{id}` - під префіксом `entries(code)`, тож інвалідується разом із переліком і деревом. */
function detailKey(code: string, id: number): readonly unknown[] {
  return [...queryKeys.registries.entries(code), 'detail', id] as const;
}

/**
 * Форма атрибутів вузла структури (NAE plan N2): код, назва мовами каталогу й поля довідника.
 * Створення - під батьком на вказаному рівні (кнопка «Add child»), правка - вибраного вузла.
 *
 * ⛔ Один ендпоінт на створення й правку (`POST …/entries`, `id: null` - створення): рівень і батько їдуть у тілі,
 * порядок рівнів і унікальність коду перевіряє сервер, а його відмова не губиться - біля поля коду або сповіщенням.
 */
export function NodeAttributesForm(props: NodeAttributesFormProps): JSX.Element {
  const { target, registryCode } = props;
  const detail = useQuery({
    queryKey: detailKey(registryCode, target.mode === 'edit' ? Number(target.node.id) : 0),
    queryFn: () =>
      apiFetch<NodeDetail>(
        `/api/v1/registries/${encodeURIComponent(registryCode)}/entries/${String(target.mode === 'edit' ? target.node.id : 0)}`,
      ),
    enabled: target.mode === 'edit',
    // ⚠ Форма бере стан із відповіді один раз при монтуванні: кешована з минулого відкриття відповідь підставила б
    // те, чого вже немає.
    gcTime: 0,
  });
  const pending = usePendingLoading(detail.isPending && target.mode === 'edit');

  if (target.mode === 'create') {
    return <Form {...props} initial={EmptyNodeForm} parentEntryId={target.parent === null ? null : Number(target.parent.id)} />;
  }

  if (detail.error !== null && detail.error !== undefined) {
    return <ErrorAlert error={detail.error} onRetry={() => void detail.refetch()} />;
  }

  if (detail.data === undefined) {
    return pending ? <Skeleton height={160} radius="sm" data-nae-form="pending" /> : <div aria-busy="true" data-nae-form="pending" />;
  }

  return <Form {...props} initial={formOfDetail(detail.data)} parentEntryId={detail.data.parentEntryId} />;
}

function Form({
  registryCode,
  registryId,
  fields,
  target,
  parentPath = [],
  readOnly,
  onSaved,
  onCancel,
  initial,
  parentEntryId,
}: NodeAttributesFormProps & { readonly initial: NodeFormState; readonly parentEntryId: number | null }): JSX.Element {
  const queryClient = useQueryClient();
  const [code, setCode] = useState(initial.code);
  const [display, setDisplay] = useState<LocalizedValue>({ ...initial.display });
  const [values, setValues] = useState<Record<string, string>>({ ...initial.values });
  const [tried, setTried] = useState(false);
  const [serverProblem, setServerProblem] = useState<ServerFieldProblem | null>(null);

  const current: NodeFormState = { code, display, values };
  const problems: NodeFormProblems = validateNode(fields, current);
  const dirty = JSON.stringify(current) !== JSON.stringify(initial);
  const dirtyRef = useRef(dirty);
  dirtyRef.current = dirty;

  // Незбережені атрибути не губляться мовчки при виході зі сторінки чи закритті вкладки.
  useEffect(
    () => registerUnsavedSource('nae-node-form', { hasUnsaved: () => dirtyRef.current && !readOnly }),
    [readOnly],
  );

  const save = useMutation({
    mutationFn: () =>
      apiFetch<EntryIdResponse>(`/api/v1/registries/${encodeURIComponent(registryCode)}/entries`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(buildNodeBody({ registryId, fields, target, parentEntryId, initial, current })),
      }),
    onSuccess: async (result) => {
      await invalidateRegistryTree(queryClient, registryCode);
      showDone(target.mode === 'create' ? t('registries.entryCreated') : t('registries.entrySaved'));
      onSaved(result.id);
    },
    onError: (error: unknown) => {
      // Відмова про код - біля поля коду; решта - сповіщенням (текст бере каталог за `messageKey`).
      setServerProblem(serverFieldProblem(error));
      showApiError(error);
    },
  });
  const loading = usePendingLoading(save.isPending);

  const submit = (): void => {
    if (save.isPending || readOnly) return;
    setTried(true);
    if (hasProblems(problems)) return;
    setServerProblem(null);
    save.mutate();
  };

  const parentLabel =
    target.mode === 'create'
      ? parentPath.length === 0
        ? t('permits.nae.form.parentRoot')
        : parentPath.map((node) => node.label).join(' › ')
      : null;

  const codeError =
    serverProblem === 'codeTaken'
      ? t('permits.nae.form.codeTaken')
      : serverProblem === 'codeAutomatic'
        ? t('permits.nae.form.codeAutomatic')
        : tried && problems.code !== undefined
          ? t('permits.nae.form.codeRequired')
          : undefined;

  return (
    <form
      data-nae-form={target.mode}
      aria-label={target.mode === 'create' ? t('permits.nae.form.createTitle', { level: target.level.name }) : t('permits.nae.form.editTitle')}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
      noValidate
    >
      <Stack gap="sm">
        <Text fw={600} size="sm">
          {target.mode === 'create' ? t('permits.nae.form.createTitle', { level: target.level.name }) : t('permits.nae.form.editTitle')}
        </Text>
        {parentLabel !== null && (
          <Text size="sm" c="dimmed" data-nae-parent="">
            {t('permits.nae.form.parent', { path: parentLabel })}
          </Text>
        )}

        <Fieldset variant="unstyled" disabled={readOnly}>
          <Stack gap="sm">
            <TextInput
              label={t('registries.code')}
              description={t('registries.entryCodeHint')}
              value={code}
              error={codeError}
              onChange={(event) => {
                const next = event.currentTarget.value;
                setCode(next);
                setServerProblem(null);
              }}
              data-autofocus={target.mode === 'create' ? true : undefined}
            />

            <LocalizedInput label={t('registries.name')} value={display} onChange={setDisplay} required />
            {tried && problems.display !== undefined && (
              <Text size="sm" c="statusError" role="alert" data-nae-name-error="">
                {t('permits.nae.form.nameRequired')}
              </Text>
            )}

            {fields.map((field) => (
              <TextInput
                key={field.id}
                label={localized(field.nameL10n) || field.code}
                description={`${field.code} · ${dataTypeLabel(field.dataType)}`}
                required={field.isRequired}
                value={values[field.code] ?? ''}
                error={tried && problems.values[field.code] !== undefined ? t('permits.nae.form.fieldRequired') : undefined}
                onChange={(event) => {
                  // ⛔ `currentTarget` обнуляється після обробника, а апдейтер `setValues` читає ліниво (StrictMode).
                  const next = event.currentTarget.value;
                  setValues((previous) => ({ ...previous, [field.code]: next }));
                }}
              />
            ))}
          </Stack>
        </Fieldset>

        {!readOnly && (
          <Group justify="flex-end">
            {onCancel !== undefined && (
              <Button variant="default" type="button" onClick={onCancel}>
                {t('common.cancel')}
              </Button>
            )}
            <Button type="submit" loading={loading} disabled={target.mode === 'edit' && !dirty}>
              {t('common.save')}
            </Button>
          </Group>
        )}
      </Stack>
    </form>
  );
}
