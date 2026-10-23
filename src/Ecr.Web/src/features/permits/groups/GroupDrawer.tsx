import { useEffect, useMemo, useRef, useState, type JSX } from 'react';
import { Button, Group, MultiSelect, Skeleton, Stack, Switch, Text, TextInput } from '@mantine/core';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { serverMessage } from '@/features/registries/rc816/serverMessage';
import { language, t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { DetailDrawer, useDetailPanel } from '@/shared/ui/DetailDrawer';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import { showApiError, showDone } from '@/shared/ui/notify';
import { problemText } from '@/shared/ui/problemText';
import { EcrApiError } from '@/api/client';
import { typeName } from '../types/typesListModel';
import { useAllPermitTypes } from '../types/useTypes';
import { getPermitGroup, type PermitGroupDto } from './groupsApi';
import {
  GroupDescriptionMax,
  GroupErrorCodes,
  createBody,
  draftOf,
  draftProblems,
  emptyDraft,
  fieldOfMessageKey,
  groupName,
  isGroupUsed,
  messageKeyOf,
  metaChanged,
  typesBody,
  typesChanged,
  updateBody,
  NewGroupPanel,
  type GroupDraft,
  type GroupField,
} from './groupsModel';
import { LocalizedTextarea } from './LocalizedTextarea';
import { useDirtyDrawerExit } from './useDirtyDrawerExit';
import { useCreateGroup, useDeleteGroup, useReplaceGroupTypes, useUpdateGroup } from './useGroups';

const FormId = 'permit-group-form';

type FieldErrors = Partial<Record<GroupField | 'description', string>>;

export interface GroupDrawerProps {
  /** Група, що редагується; `null` - нова. */
  readonly group: PermitGroupDto | null;
  /** Право керувати групами (`Permit.EditType`, без симуляції); без нього шторка лише для читання. */
  readonly canEdit: boolean;
  /**
   * Чи є в шторці незбережене, що не можна кинути мовчки (`guarded`): сторінка питає «Discard», коли людина
   * клікає по рядку ІНШОГО запису (на широкому екрані без затемнення), бо шторка перемонтується `key={code}`.
   */
  readonly onDirtyChange?: (dirty: boolean) => void;
}

/**
 * Шторка «New / Edit group» (макет екрана Groups немає: нейтральний дефолт за аналогією зі шторкою списків, KIT §6.9,
 * поля - KIT §6.8). Назва й опис мовами каталогу, код (лише при створенні), активність, склад - опубліковані типи.
 *
 * ⛔ Токен `rowVersion` береться при відкритті шторки і оновлюється лише після власного збереження чи після
 * `409 ECR-PRM-4099`: фонове перечитання переліку не підміняє його, інакше чужа правка була б мовчки перезаписана.
 * ⚠ Збереження існуючої групи - до двох запитів (`PUT permit-groups/{code}` для назви/опису/активності і
 * `PUT …/types` для складу); другий іде з токеном, який повернув перший.
 */
export function GroupDrawer({ group, canEdit, onDirtyChange }: GroupDrawerProps): JSX.Element {
  const isNew = group === null;
  const [, setPanel] = useDetailPanel();
  const panelId = isNew ? NewGroupPanel : group.code;
  const lang = language();

  const [original, setOriginal] = useState<GroupDraft>(() => (group === null ? emptyDraft() : draftOf(group)));
  const [draft, setDraft] = useState<GroupDraft>(original);
  const [rowVersion, setRowVersion] = useState(group?.rowVersion ?? '');
  const [errors, setErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [discarding, setDiscarding] = useState(false);

  const create = useCreateGroup();
  const update = useUpdateGroup();
  const replace = useReplaceGroupTypes();
  const remove = useDeleteGroup();
  const busy = create.isPending || update.isPending || replace.isPending;
  const saving = usePendingLoading(busy);
  const types = useAllPermitTypes();

  // Опубліковані типи для вибору складу + типи, що вже в групі (навіть якщо в переліку їх не видно).
  const typeOptions = useMemo(() => {
    const published = (types.data ?? []).filter((item) => item.status === 'Published');
    const options = published.map((item) => ({ value: item.code, label: typeName(item, lang) }));
    const known = new Set(options.map((option) => option.value));
    const extra = (group?.types ?? [])
      .filter((item) => !known.has(item.code))
      .map((item) => ({ value: item.code, label: groupTypeLabel(item.nameL10n, item.code, lang) }));

    return [...options, ...extra];
  }, [types.data, group, lang]);

  // Шторка змонтована вже відкритою: на широкому екрані пастки фокуса немає, тож ставимо фокус на перше поле сами.
  useEffect(() => {
    const timer = window.setTimeout(() => {
      const root = [...document.querySelectorAll<HTMLElement>('[data-panel]')].find((element) => element.getAttribute('data-panel') === panelId);
      const dialog = root?.matches('[role="dialog"]') === true ? root : (root?.querySelector<HTMLElement>('[role="dialog"]') ?? null);
      if (dialog === null || dialog.contains(document.activeElement)) return;
      const first = dialog.querySelector<HTMLElement>('input:not([disabled]):not([type="hidden"])');
      (first ?? dialog).focus({ preventScroll: true });
    }, 0);

    return () => window.clearTimeout(timer);
  }, [panelId]);

  const used = group !== null && isGroupUsed(group);
  const dirty = metaChanged(draft, original) || typesChanged(draft, original) || (isNew && draft.code !== '');
  // Тип прибрано зі складу використовуваної групи: значення, вже введені в документах, лишаються (сервер перевіряє лише змінені).
  const compositionShrunk = !isNew && used && original.typeCodes.some((code) => !draft.typeCodes.includes(code));

  function close(): void {
    setPanel(null);
  }

  // Вийти з незбереженим - лише через підтвердження «Discard» (Esc, хрестик, затемнення й Cancel); чиста форма закривається одразу.
  // ⛔ Не `onExitUnsaved => false` без підтвердження: введене пропало б мовчки (клас дефекту «Assign month»).
  const guarded = canEdit && dirty && !busy;
  useDirtyDrawerExit(panelId, guarded && !confirmingDelete && !discarding, () => setDiscarding(true));

  const reportDirty = useRef(onDirtyChange);
  reportDirty.current = onDirtyChange;
  useEffect(() => {
    reportDirty.current?.(guarded);

    return () => reportDirty.current?.(false);
  }, [guarded]);

  function requestClose(): void {
    if (guarded) setDiscarding(true);
    else close();
  }

  function change(next: Partial<GroupDraft>, field?: GroupField | 'description'): void {
    setDraft((current) => ({ ...current, ...next }));
    if (field !== undefined) setErrors((current) => ({ ...current, [field]: undefined }));
    setFormError(null);
  }

  /** Відмова сервера: поле з `messageKey`, інакше смуга у формі; конкурентність і «групи немає» - окремо. */
  async function fail(error: unknown): Promise<void> {
    const info = messageKeyOf(error);
    const code = error instanceof EcrApiError ? error.problem.errorCode : '';

    if (code === GroupErrorCodes.conflict && group !== null) {
      // Хтось змінив групу: показуємо причину й підтягуємо свіжий стан (токен + поля); правку людина повторює.
      showApiError(error);
      try {
        const fresh = await getPermitGroup(group.code);
        const next = draftOf(fresh);
        setOriginal(next);
        setDraft(next);
        setRowVersion(fresh.rowVersion);
        setErrors({});
        setFormError(t('permits.groups.drawer.conflict'));
      } catch (readError) {
        showApiError(readError);
      }

      return;
    }

    if (code === GroupErrorCodes.notFound) {
      showApiError(error);
      close();

      return;
    }

    const field = info === null ? null : fieldOfMessageKey(info.key);
    if (info !== null && field !== null) {
      setErrors((current) => ({ ...current, [field]: serverMessage(info.key, info.params) }));

      return;
    }

    const shown = problemText(error);
    if (shown.detail !== null) setFormError(shown.detail);
    else showApiError(error);
  }

  async function submit(): Promise<void> {
    if (!canEdit || busy) return;

    const problems = draftProblems(draft, isNew);
    setErrors(problems);
    setFormError(null);
    if (Object.keys(problems).length > 0) return;

    try {
      if (group === null) {
        await create.mutateAsync(createBody(draft));
        showDone(t('permits.groups.drawer.created', { name: groupName({ code: draft.code, nameL10n: draft.name }, lang) }));
        close();

        return;
      }

      if (!metaChanged(draft, original) && !typesChanged(draft, original)) {
        close();

        return;
      }

      let version = rowVersion;
      if (metaChanged(draft, original)) {
        const saved = await update.mutateAsync({ code: group.code, body: updateBody(draft, version) });
        version = saved.rowVersion;
        setRowVersion(version);
        setOriginal((current) => ({ ...current, name: draft.name, description: draft.description, isActive: draft.isActive }));
      }
      if (typesChanged(draft, original)) {
        const saved = await replace.mutateAsync({ code: group.code, body: typesBody(draft, version) });
        setRowVersion(saved.rowVersion);
      }

      showDone(t('permits.groups.drawer.saved', { name: groupName({ code: group.code, nameL10n: draft.name }, lang) }));
      close();
    } catch (error) {
      await fail(error);
    }
  }

  function confirmDelete(): void {
    if (group === null) return;
    remove.mutate(group.code, {
      onSuccess: () => {
        setConfirmingDelete(false);
        showDone(t('permits.groups.drawer.deleted', { name: groupName(group, lang) }));
        close();
      },
      onError: (error) => {
        setConfirmingDelete(false);
        // `409 ECR-PRM-4098` (використовується) і решта: людське повідомлення за `messageKey`; перелік перечитаний хуком.
        showApiError(error);
      },
    });
  }

  const title = isNew ? t('permits.groups.drawer.newTitle') : groupName({ code: group.code, nameL10n: draft.name }, lang);
  const readOnly = !canEdit;

  return (
    <DetailDrawer
      panelId={panelId}
      title={title}
      subtitle={isNew ? undefined : group.code}
      closeLabel={t('permits.groups.drawer.close')}
      size="lg"
      // Esc у підтвердженні видалення закриває лише його (обидва Modal слухають keydown на window).
      // З незбереженим Esc обробляє `useDirtyDrawerExit` (підтвердження), а не шторка.
      closeOnEscape={!confirmingDelete && !guarded}
      footer={
        canEdit ? (
          <Group justify="space-between" gap="xs" w="100%" wrap="nowrap">
            {!isNew && !used ? (
              <Button variant="default" color="statusError" data-group-delete="" onClick={() => setConfirmingDelete(true)}>
                {t('permits.groups.drawer.delete')}
              </Button>
            ) : (
              <span />
            )}
            <Group gap="xs" wrap="nowrap">
              <Button variant="default" onClick={requestClose}>
                {t('permits.groups.drawer.cancel')}
              </Button>
              <Button type="submit" form={FormId} loading={saving} disabled={!isNew && !dirty}>
                {isNew ? t('permits.groups.drawer.create') : t('permits.groups.drawer.save')}
              </Button>
            </Group>
          </Group>
        ) : undefined
      }
    >
      <form
        id={FormId}
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void submit();
        }}
      >
        <Stack gap="md" data-group-drawer={isNew ? 'new' : 'edit'}>
          {readOnly && <Text size="sm" c="dimmed">{t('permits.groups.drawer.readOnly')}</Text>}
          {formError !== null && <Banner tone="danger" text={formError} />}

          <TextInput
            label={t('permits.groups.field.code')}
            description={isNew ? t('permits.groups.field.codeHint') : undefined}
            value={draft.code}
            disabled={!isNew || readOnly}
            required={isNew}
            maxLength={59}
            error={errors.code}
            onChange={(event) => change({ code: event.currentTarget.value }, 'code')}
            data-group-field="code"
          />

          <Stack gap="xs" data-group-field="name">
            <LocalizedInput
              label={t('permits.groups.field.name')}
              description={t('permits.groups.field.nameHint')}
              required
              value={draft.name}
              onChange={(name) => change({ name }, 'name')}
            />
            {errors.name !== undefined && (
              <Text size="xs" c="statusError" role="alert">
                {errors.name}
              </Text>
            )}
          </Stack>

          <LocalizedTextarea
            label={t('permits.groups.field.description')}
            description={t('permits.groups.field.descriptionHint')}
            value={draft.description}
            maxLength={GroupDescriptionMax}
            disabled={readOnly}
            error={errors.description}
            onChange={(description) => change({ description }, 'description')}
          />

          <Switch
            label={t('permits.groups.field.active')}
            description={t('permits.groups.field.activeHint')}
            checked={draft.isActive}
            disabled={readOnly}
            onChange={(event) => change({ isActive: event.currentTarget.checked })}
            data-group-field="active"
          />

          {types.error !== null && <ErrorAlert error={types.error} onRetry={() => void types.refetch()} />}
          {types.error === null && types.isPending && <Skeleton height={60} radius="sm" data-group-types="pending" />}
          {types.error === null && !types.isPending && (
            <MultiSelect
              label={t('permits.groups.field.types')}
              description={t('permits.groups.field.typesHint')}
              data={typeOptions}
              value={[...draft.typeCodes]}
              searchable
              disabled={readOnly}
              nothingFoundMessage={t('permits.groups.field.typesEmpty')}
              error={errors.types}
              onChange={(typeCodes) => change({ typeCodes }, 'types')}
              data-group-field="types"
            />
          )}

          {used && group !== null && (
            <Text size="sm" c="dimmed" data-group-used="">
              {t('permits.groups.drawer.usedNote', { templates: String(group.usedInTemplates), columns: String(group.usedInColumns) })}
            </Text>
          )}
          {compositionShrunk && (
            <Text size="sm" c="dimmed">
              {t('permits.groups.drawer.shrinkNote')}
            </Text>
          )}
        </Stack>
      </form>

      <ConfirmModal
        opened={discarding}
        title={t('permits.groups.drawer.discardTitle')}
        text={t('permits.groups.drawer.discardText')}
        verb={t('permits.groups.drawer.discardVerb')}
        cancelLabel={t('permits.groups.drawer.keepEditing')}
        onConfirm={() => {
          setDiscarding(false);
          close();
        }}
        onClose={() => setDiscarding(false)}
      />

      {!isNew && (
        <ConfirmModal
          opened={confirmingDelete}
          title={t('permits.groups.drawer.deleteTitle', { name: groupName({ code: group.code, nameL10n: draft.name }, lang) })}
          text={t('permits.groups.drawer.deleteText')}
          verb={t('permits.groups.drawer.deleteVerb')}
          isPending={remove.isPending}
          onConfirm={confirmDelete}
          onClose={() => setConfirmingDelete(false)}
        />
      )}
    </DetailDrawer>
  );
}

function groupTypeLabel(nameL10n: Readonly<Record<string, string>>, code: string, lang: string): string {
  const name = nameL10n[lang] ?? nameL10n['en'] ?? Object.values(nameL10n)[0] ?? '';

  return name === '' ? code : name;
}
