import { useEffect, useRef, useState, type JSX } from 'react';
import { Anchor, Button, Fieldset, Group, Skeleton, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { invalidateRegistryTree } from '@/features/registries/tree/useRegistryTree';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput, type LocalizedValue } from '@/shared/ui/LocalizedInput';
import { showApiError, showDone } from '@/shared/ui/notify';
import type { TreeNode } from '@/shared/ui/TreeEditor';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import {
  EmptyNodeForm,
  NaeAreaField,
  NaeCodeField,
  buildNodeBody,
  fieldsForLevel,
  formOfDetail,
  hasProblems,
  isServerField,
  serverFieldProblem,
  serverKeyHolder,
  serverNaeCodeHolder,
  showsServerField,
  targetLevelCode,
  validateNode,
  type NaeCodeHolder,
  type NodeDetail,
  type NodeFormProblems,
  type NodeFormState,
  type NodeFormTarget,
  type ServerFieldProblem,
} from './nodeAttributes';
import { AreaSelect } from './AreaSelect';

type EntryIdResponse = components['schemas']['RegistryEntryIdResponse'];
type RegistryField = components['schemas']['RegistryFieldDto'];

export interface NodeAttributesFormProps {
  readonly registryCode: string;
  readonly registryId: number;
  /** Код запису видає система (`CodeMode = Auto`): поля коду для нового вузла немає, у правці він лише показаний. */
  readonly codeAutomatic: boolean;
  readonly fields: readonly RegistryField[];
  readonly target: NodeFormTarget;
  /** Шлях до батька (для підпису «куди додається»); порожній - корінь. */
  readonly parentPath?: readonly TreeNode[];
  /** Без права запису: поля видно, але не змінити, «Save» немає. */
  readonly readOnly: boolean;
  /** Збережено; `id` - запис (для вибору в дереві після створення). */
  readonly onSaved: (id: number) => void;
  readonly onCancel?: (() => void) | undefined;
  /** «Відкрити вузол» з відмови «номер NAE зайнятий»: тримач номера. Без нього посилання не показується. */
  readonly onOpenNode?: ((id: number) => void) | undefined;
}

/** Поля, від яких залежить ідентичність номера NAE: сам номер і коди регіону/району (каскад на джерела під вузлом). */
function ownsNaeIdentity(field: RegistryField): boolean {
  return field.code === NaeCodeField || field.code === 'RegionCode' || field.code === 'AreaCode';
}

/** Ключ `GET …/entries/{id}` - під префіксом `entries(code)`, тож інвалідується разом із переліком і деревом. */
function detailKey(code: string, id: number): readonly unknown[] {
  return [...queryKeys.registries.entries(code), 'detail', id] as const;
}

function detailPath(code: string, id: number): string {
  return `/api/v1/registries/${encodeURIComponent(code)}/entries/${String(id)}`;
}

/**
 * Форма вузла структури NAE за ТИПОМ вузла (рівнем реєстру, 03.11): код, назва трьома мовами (англійська обов'язкова) і
 * поля довідника, що належать саме цьому рівню або всім рівням. Поля, які веде сервер (регіон і район - з предків,
 * шлях AF - із завантаження), показані текстом, а не полем вводу.
 * Створення - під батьком на вказаному рівні (кнопка «Add child»), правка - вибраного вузла.
 *
 * ⛔ Один ендпоінт на створення й правку (`POST …/entries`, `id: null` - створення): рівень і батько їдуть у тілі,
 * порядок рівнів і унікальність номера NAE перевіряє сервер, а його відмова не губиться - біля поля або сповіщенням.
 */
export function NodeAttributesForm(props: NodeAttributesFormProps): JSX.Element {
  const { target, registryCode } = props;
  const detail = useQuery({
    queryKey: detailKey(registryCode, target.mode === 'edit' ? Number(target.node.id) : 0),
    queryFn: () => apiFetch<NodeDetail>(detailPath(registryCode, target.mode === 'edit' ? Number(target.node.id) : 0)),
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
  codeAutomatic,
  fields,
  target,
  parentPath = [],
  readOnly,
  onSaved,
  onCancel,
  onOpenNode,
  initial,
  parentEntryId,
}: NodeAttributesFormProps & { readonly initial: NodeFormState; readonly parentEntryId: number | null }): JSX.Element {
  const queryClient = useQueryClient();
  // Базовий стан: те, що збережено на сервері. Після збереження правки він береться з відповіді сервера заново, бо сервер
  // нормалізує значення (номер `36_pod` -> `0036_pod`) і виводить регіон та район.
  const [baseline, setBaseline] = useState<NodeFormState>(initial);
  const [code, setCode] = useState(initial.code);
  const [display, setDisplay] = useState<LocalizedValue>({ ...initial.display });
  const [values, setValues] = useState<Record<string, string>>({ ...initial.values });
  const [tried, setTried] = useState(false);
  const [serverProblem, setServerProblem] = useState<ServerFieldProblem | null>(null);
  const [holder, setHolder] = useState<string | null>(null);
  const [naeHolder, setNaeHolder] = useState<NaeCodeHolder | null>(null);

  // Поля свого типу: одні вводить людина, інші веде сервер.
  const levelFields = fieldsForLevel(fields, targetLevelCode(target));
  const levelCode = targetLevelCode(target);
  const inputFields = levelFields.filter((field) => !isServerField(field, levelCode));
  const serverFields = levelFields.filter((field) => isServerField(field, levelCode) && showsServerField(field, levelCode));

  const current: NodeFormState = { code, display, values };
  const problems: NodeFormProblems = validateNode(inputFields, current, codeAutomatic);
  const dirty = JSON.stringify(current) !== JSON.stringify(baseline);
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
        body: JSON.stringify(
          buildNodeBody({ registryId, fields: inputFields, target, parentEntryId, initial: baseline, current, codeAutomatic }),
        ),
      }),
    onSuccess: async (result) => {
      await invalidateRegistryTree(queryClient, registryCode);
      if (target.mode === 'edit') {
        // Нормалізований номер і виведені регіон/район - зі збереженого запису, а не з того, що ввела людина.
        try {
          const fresh = await queryClient.fetchQuery({
            queryKey: detailKey(registryCode, Number(target.node.id)),
            queryFn: () => apiFetch<NodeDetail>(detailPath(registryCode, Number(target.node.id))),
            staleTime: 0,
          });
          const next = formOfDetail(fresh);
          setBaseline(next);
          setCode(next.code);
          setDisplay({ ...next.display });
          setValues({ ...next.values });
        } catch {
          // Збережено, а перечитати не вдалося: форма лишається з введеним, наступне відкриття прочитає сервер.
        }
      }
      setTried(false);
      showDone(target.mode === 'create' ? t('registries.entryCreated') : t('registries.entrySaved'));
      onSaved(result.id);
    },
    onError: (error: unknown) => {
      // Відмова про поле - біля поля; решта - сповіщенням (текст бере каталог за `messageKey`).
      setServerProblem(serverFieldProblem(error));
      setHolder(serverKeyHolder(error));
      setNaeHolder(serverNaeCodeHolder(error));
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

  const fieldError = (field: RegistryField): JSX.Element | string | undefined => {
    // Дубль номера NAE - біля поля, що його спричинило: номер на SRC або код регіону/району на REGION/AREA, чий каскад
    // на джерела під ним дав би дубль (сервер нічого не змінює).
    if (ownsNaeIdentity(field) && serverProblem === 'naeCodeTaken' && naeHolder !== null) {
      return (
        <span data-nae-code-taken="">
          {t('permits.nae.form.naeCodeTaken', {
            naeCode: naeHolder.naeCode,
            holder: naeHolder.entryCode,
            region: naeHolder.region,
            area: naeHolder.area,
          })}{' '}
          {onOpenNode !== undefined && naeHolder.entryId !== null && (
            <Anchor component="button" type="button" size="sm" onClick={() => onOpenNode(naeHolder.entryId ?? 0)} data-nae-code-taken-open="">
              {t('permits.nae.form.naeCodeTakenOpen')}
            </Anchor>
          )}
        </span>
      );
    }

    return tried && problems.values[field.code] !== undefined ? t('permits.nae.form.fieldRequired') : undefined;
  };

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
            {serverProblem === 'keyTaken' && (
              <Text size="sm" c="statusError" role="alert" data-nae-key-error="">
                {holder === null ? t('permits.nae.form.keyTaken') : t('permits.nae.form.keyTakenBy', { holder })}
              </Text>
            )}
            {!(codeAutomatic && target.mode === 'create') && (
              <TextInput
                label={t('registries.code')}
                description={codeAutomatic ? undefined : t('registries.entryCodeHint')}
                value={code}
                error={codeError}
                readOnly={codeAutomatic}
                onChange={(event) => {
                  const next = event.currentTarget.value;
                  setCode(next);
                  setServerProblem(null);
                }}
                data-autofocus={target.mode === 'create' ? true : undefined}
              />
            )}

            <LocalizedInput label={t('registries.name')} description={t('permits.nae.form.nameHint')} value={display} onChange={setDisplay} required />
            {((tried && problems.display !== undefined) || serverProblem === 'nameEnRequired') && (
              <Text size="sm" c="statusError" role="alert" data-nae-name-error="">
                {t('permits.nae.form.nameRequired')}
              </Text>
            )}

            {inputFields.map((field) => field.code === NaeAreaField ? (
              <AreaSelect
                key={field.id}
                label={localized(field.nameL10n) || field.code}
                value={values[field.code] ?? ''}
                error={fieldError(field)}
                onChange={(next) => {
                  setValues((previous) => ({ ...previous, [field.code]: next }));
                  setServerProblem(null);
                }}
                data-nae-area=""
              />
            ) : (
              <TextInput
                key={field.id}
                label={localized(field.nameL10n) || field.code}
                description={field.code === NaeCodeField ? t('permits.nae.form.naeCodeHint') : undefined}
                required={field.isRequired}
                value={values[field.code] ?? ''}
                error={fieldError(field)}
                onChange={(event) => {
                  // ⛔ `currentTarget` обнуляється після обробника, а апдейтер `setValues` читає ліниво (StrictMode).
                  const next = event.currentTarget.value;
                  setValues((previous) => ({ ...previous, [field.code]: next }));
                  if (ownsNaeIdentity(field)) setServerProblem(null);
                }}
              />
            ))}

            {serverFields.length > 0 && (
              <Stack gap="xs" data-nae-derived="">
                <Text size="xs" c="dimmed">
                  {target.mode === 'create' ? t('permits.nae.form.derivedCreate') : t('permits.nae.form.derived')}
                </Text>
                {serverFields.map((field) => (
                  <Group key={field.id} gap="xs" wrap="nowrap" data-nae-derived-field={field.code}>
                    <Text size="sm" fw={500} style={{ minWidth: '9rem' }}>
                      {localized(field.nameL10n) || field.code}
                    </Text>
                    <Text size="sm">{target.mode === 'create' ? '—' : (baseline.values[field.code] ?? '') || '—'}</Text>
                  </Group>
                ))}
              </Stack>
            )}
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
