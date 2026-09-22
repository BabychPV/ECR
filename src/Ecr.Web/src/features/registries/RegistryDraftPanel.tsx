import { useState, type JSX } from 'react';
import { Button, Group, Skeleton, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import type { SaveRegistryDefinitionDto } from '@/api/types';
import { formatDateTime } from '@/shared/format';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { Banner } from '@/shared/ui/Banner';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';
import {
  discardRegistryDraft,
  draftConflictOf,
  getRegistryDraft,
  publishRegistryDefinition,
  registryDraftKey,
  saveAndPublishRegistryDefinition,
  saveRegistryDraft,
  type DraftConflict,
} from './registryDraft';

/**
 * Чернетка опису довідника і публікація (`BE-24` крок 2).
 *
 * ⛔ Збереження і публікація — ДВІ дії, а не одна кнопка з підтвердженням.
 * Права на них різні (`Registry.EditDefinition` проти `Registry.Publish`), і
 * саме в цьому сенс кроку: описати зміну може той, хто розуміє довідник, а
 * випустити її в дію — той, хто відповідає за наслідки для вже збережених
 * записів. Одна кнопка «зберегти й опублікувати» звела б два рішення в одне й
 * віддала б обидва тому, хто має лише перше право.
 *
 * ⛔ Прямий `PUT …/definition` звідси НЕ викликається. Він на сервері лишився,
 * але тепер вимагає обох прав одразу — тобто це «зберегти й одразу
 * опублікувати», окремий, свідомий шлях. Конструктор іде через чернетку.
 */
export function RegistryDraftPanel({
  code,
  request,
  reason,
  onReasonChange,
}: {
  readonly code: string;

  /** Повний стан форми; `null` — форма ще не готова до збереження. */
  readonly request: SaveRegistryDefinitionDto | null;

  readonly reason: string;
  readonly onReasonChange: (value: string) => void;
}): JSX.Element {
  const session = useSession();
  const queryClient = useQueryClient();

  const mayEdit = can(session.data, 'Registry.EditDefinition');

  // ⛔ Окреме право, а не `mayEdit`: сервер перевіряє саме `Registry.Publish`
  // (`PublishRegistryDefinitionHandler.Permission`), і кнопка, показана за
  // правом правити, вела б редактора без права публікації у відому `403`.
  const mayPublish = can(session.data, 'Registry.Publish');

  const state = useQuery({
    queryKey: registryDraftKey(code),
    queryFn: () => getRegistryDraft(code),

    // ⚠ Те саме рішення, що й для опису: відповідь засіває ФОРМУ, і
    // перечитування при поверненні фокуса стерло б недописане правило.
    refetchOnWindowFocus: false,
    staleTime: Number.POSITIVE_INFINITY,
  });

  const draft = state.data?.draft ?? null;
  const rowVersion = draft?.rowVersion ?? null;

  const [confirm, setConfirm] = useState<'publish' | 'discard' | 'saveAndPublish' | null>(null);

  const reload = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: registryDraftKey(code) });
  };

  const save = useMutation({
    meta: { handled: true },
    mutationFn: () =>
      saveRegistryDraft(code, {
        fields: request?.fields ?? [],
        rules: request?.rules ?? [],
        reason: request?.reason ?? '',
        rowVersion,
      }),
    onSuccess: async () => {
      await reload();
      showDone(t('registries.draftSaved'));
    },
  });

  const publish = useMutation({
    meta: { handled: true },
    mutationFn: () => publishRegistryDefinition(code, rowVersion ?? ''),
    onSettled: () => setConfirm(null),
    onSuccess: async (result) => {
      // ⛔ Разом з описом — і ІСТОРІЯ: публікація пише в журнал структурних
      // змін, і залишити вкладку історії на старому кеші означало б показати
      // версію, якої вже немає, поруч із записом, якого ще немає.
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.definition(code) });
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.history(code) });
      await reload();
      showDone(t('registries.definitionPublished', { version: result.definitionVersion }));
    },
  });

  /**
   * Прямий `PUT …/definition` — «зберегти й одразу опублікувати».
   *
   * ⛔ Кнопка стоїть лише тоді, коли чернетки НЕМАЄ. Поруч із наявною
   * чернеткою ця дія була б пасткою: вона підняла б версію опису повз
   * чернетку, і та мовчки стала б непублікованою (`definitionDraftStale`) —
   * тобто «швидкий шлях» ламав би роботу, яку хтось уже зберіг.
   */
  const saveAndPublish = useMutation({
    meta: { handled: true },
    mutationFn: () =>
      saveAndPublishRegistryDefinition(code, {
        fields: request?.fields ?? [],
        rules: request?.rules ?? [],
        reason: request?.reason ?? '',
      }),
    onSettled: () => setConfirm(null),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.definition(code) });
      await queryClient.invalidateQueries({ queryKey: queryKeys.registries.history(code) });
      await reload();
      showDone(t('registries.definitionPublished', { version: result.definitionVersion }));
    },
  });

  const discard = useMutation({
    meta: { handled: true },
    mutationFn: () => discardRegistryDraft(code, rowVersion ?? ''),
    onSettled: () => setConfirm(null),
    onSuccess: async () => {
      await reload();
      showDone(t('registries.draftDiscarded'));
    },
  });

  const failures = [save.error, publish.error, discard.error, saveAndPublish.error];
  const conflict = failures.map(draftConflictOf).find((item) => item !== null) ?? null;
  const general = failures.find((error) => error !== null && draftConflictOf(error) === null) ?? null;

  /*
   * ⛔ Поверх нерозв'язаного конфлікту версій нічого не шлемо: та сама версія
   * дала б ту саму відмову. `stale` сюди НЕ входить — там версія чернетки
   * правильна, розійшовся опублікований опис, і рішення «публікувати попри це
   * чи спершу перезберегти» належить людині, а не формі.
   */
  const blocked = conflict !== null && conflict.kind !== 'stale';

  const takeCurrent = (): void => {
    save.reset();
    publish.reset();
    discard.reset();
    saveAndPublish.reset();
    void reload();
  };

  return (
    <Stack gap="xs" mb="sm" data-registry-draft="">
      {state.isPending && <Skeleton height={32} radius="sm" data-registry-draft-pending="" />}

      {state.error !== null && (
        <ErrorAlert error={state.error} onRetry={() => void state.refetch()} />
      )}

      {draft !== null && (
        <Banner
          tone="warning"
          title={t('registries.draftPresent')}
          text={t('registries.draftPresentHint', {
            when: formatDateTime(draft.updatedAt),
            user: draft.updatedByUserId,
          })}
          testId="registry-draft-present"
        />
      )}

      {mayEdit && (
        <Group gap="xs" align="end">
          {/* ⛔ Причина обов'язкова: опис довідника змінює те, як читаються ВЖЕ
              збережені записи, і питання «чому тут з'явилося це поле» ставлять
              через рік. У чернетці вона зберігається разом із вмістом і при
              публікації їде в журнал. */}
          <TextInput
            size="xs"
            miw={260}
            label={t('registries.reason')}
            description={t('registries.reasonHint')}
            value={reason}
            onChange={(event) => onReasonChange(event.currentTarget.value)}
            data-draft-reason=""
          />

          <Button
            size="xs"
            disabled={request === null || blocked}
            loading={save.isPending}
            onClick={() => save.mutate()}
            data-save-draft=""
          >
            {t('registries.saveDraft')}
          </Button>

          {/* ⛔ Лише без чернетки і лише з ОБОМА правами — рівно те, що
              вимагає прямий `PUT …/definition` на сервері. */}
          {mayPublish && draft === null && (
            <Button
              size="xs"
              variant="default"
              disabled={request === null || blocked}
              loading={saveAndPublish.isPending}
              onClick={() => setConfirm('saveAndPublish')}
              data-save-and-publish=""
            >
              {t('registries.saveAndPublish')}
            </Button>
          )}

          {/* ⛔ Кнопок публікації і скасування немає, доки немає чернетки:
              публікувати нема чого, скасовувати теж, і сервер відповів би
              `404`. Кнопка, заздалегідь приречена на відмову, гірша за її
              відсутність. */}
          {draft !== null && (
            <Button
              size="xs"
              variant="default"
              disabled={blocked}
              loading={discard.isPending}
              onClick={() => setConfirm('discard')}
              data-discard-draft=""
            >
              {t('registries.discardDraft')}
            </Button>
          )}

          {mayPublish && draft !== null && (
            <Button
              size="xs"
              variant="default"
              disabled={blocked}
              loading={publish.isPending}
              onClick={() => setConfirm('publish')}
              data-publish-definition=""
            >
              {t('registries.publish')}
            </Button>
          )}
        </Group>
      )}

      {conflict !== null && <DraftConflictNote code={code} conflict={conflict} onTake={takeCurrent} />}

      {general !== null && <ErrorAlert error={general} />}

      <ConfirmModal
        opened={confirm === 'publish'}
        title={t('registries.publishTitle', { code })}
        text={t('registries.publishConsequence')}
        verb={t('registries.publish')}
        danger={false}
        isPending={publish.isPending}
        onConfirm={() => publish.mutate()}
        onClose={() => setConfirm(null)}
      />

      <ConfirmModal
        opened={confirm === 'saveAndPublish'}
        title={t('registries.saveAndPublishTitle', { code })}
        text={t('registries.saveAndPublishConsequence')}
        verb={t('registries.saveAndPublish')}
        danger={false}
        isPending={saveAndPublish.isPending}
        onConfirm={() => saveAndPublish.mutate()}
        onClose={() => setConfirm(null)}
      />

      <ConfirmModal
        opened={confirm === 'discard'}
        title={t('registries.discardTitle', { code })}
        text={t('registries.discardConsequence')}
        verb={t('registries.discardDraft')}
        isPending={discard.isPending}
        onConfirm={() => discard.mutate()}
        onClose={() => setConfirm(null)}
      />
    </Stack>
  );
}

/**
 * Текст відмови — рядок КАТАЛОГУ за `messageKey` сервера.
 *
 * ⚠ Не `problemText(error)`: речення в тілі відмови написане для серверного
 * боку, а каталог має ту саму думку мовою інтерфейсу з тими самими
 * підстановками (`D-95`).
 *
 * @param code Код довідника.
 * @param conflict Розібрана відмова.
 */
function conflictText(code: string, conflict: DraftConflict): string {
  if (conflict.kind === 'stale') {
    return t('err.ECR-REG-0409.definitionDraftStale', {
      registryCode: code,
      baseVersion: conflict.baseVersion,
      currentVersion: conflict.currentVersion,
    });
  }

  if (conflict.kind === 'missing') {
    return t('err.ECR-REG-0404.definitionDraft', { registryCode: code });
  }

  return t('err.ECR-REG-0409.definitionDraftChanged', { registryCode: code });
}

/**
 * Причина відмови чернетки — ВЛАСНИМИ словами сервера, а не «не вдалося».
 *
 * ⛔ Кожен із трьох станів веде до іншої наступної дії, і саме тому вони
 * розділені: `changed` і `missing` розв'язуються перечитуванням чернетки
 * (кнопка є), `stale` — ні (опублікований опис розійшовся з тим, від якого
 * чернетка відштовхується; чернетка лишається на екрані, і що з нею робити,
 * вирішує людина).
 */
function DraftConflictNote({
  code,
  conflict,
  onTake,
}: {
  readonly code: string;
  readonly conflict: DraftConflict;
  readonly onTake: () => void;
}): JSX.Element {
  return (
    <Stack gap="xs" data-registry-draft-conflict={conflict.kind}>
      <Text size="sm" c="statusError">
        {conflictText(code, conflict)}
      </Text>

      {conflict.kind !== 'stale' && (
        <Group gap="xs">
          <Button size="xs" variant="default" onClick={onTake} data-take-current-draft="">
            {t('registries.reloadDraft')}
          </Button>
        </Group>
      )}
    </Stack>
  );
}
