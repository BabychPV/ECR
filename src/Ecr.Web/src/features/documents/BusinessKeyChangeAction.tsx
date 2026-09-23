import { useEffect, useId, useState, type JSX } from 'react';
import { Button, Group, Menu, Modal, Stack, Text, Textarea, TextInput } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import type { DocumentSummary } from '@/api/types';
import { meetsGrant, type GrantLevelName } from '@/features/workflow/SheetActions';
import { can, useSession, type MeDto } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { problemText } from '@/shared/ui/problemText';
import { showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import {
  BusinessKeyMaxLength,
  ChangeDocumentKeyPermission,
  changeDocumentBusinessKey,
} from './api';

/**
 * Чи має користувач ефективний грант **Write** (чи вищий) на проєкт.
 *
 * ⛔ Дзеркало серверного `GetCurrentUserHandler.LevelForProject` /
 * `AccessProfile.LevelFor(ResourceKind.Project, …)` — рівно того самого
 * рішення, яким `ChangeDocumentKeyHandler` перевіряє грант перед записом
 * (`profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Write`).
 * Не сходи `SheetActions.effectiveGrant`: та функція будує ланцюг
 * `Project → Sheet → Table:0 → Column:0`, потрібний ЛИШЕ трьом рішенням
 * робочого процесу, що йдуть через `AccessDecisionService` з `columnDefId: 0`
 * (сам файл це пояснює). Зміна ключа документа таким ланцюгом не йде: сервер
 * дивиться РІВНО на `Project:{id}`, без запасних рівнів, — і клієнтська
 * перевірка звужена так само навмисно, а не для економії коду.
 *
 * ⚠ Заборона виграє на будь-якому рівні (ФВ-6.6), як і скрізь: `denies`
 * перевіряється ПЕРШИМ.
 */
export function hasProjectWriteGrant(me: MeDto | undefined, projectId: number): boolean {
  if (me === undefined) return false;

  const key = `Project:${String(projectId)}`;
  if ((me.denies ?? []).includes(key)) return false;

  const level = (me.grants ?? {})[key] as GrantLevelName | undefined;
  return level !== undefined && meetsGrant(level, 'Write');
}

/**
 * Чи документ, НАСКІЛЬКИ ЙОГО ЗНАЄ КЛІЄНТ, має поданий або погоджений аркуш.
 *
 * ⛔ Дзеркало `DocumentKeyChange.EnsureChangeable`: локер лише подання й
 * погодження (`Submitted`/`Approved`) — відхилений аркуш і чернетка й далі в
 * роботі, зміна ключа їх не чіпає.
 *
 * ⚠ `sheetStates` завжди приходить разом із `DocumentSummary` (як і в
 * `isKnownDraft`, `DeleteDocumentAction.tsx`), тож «стан невідомий» тут не
 * трапляється, доки сам документ не завантажився: аркуш без запису стану —
 * `Draft`, тобто не заблокований.
 */
export function hasLockedSheet(sheetStates: Readonly<Record<string, string>>): boolean {
  return Object.values(sheetStates).some((state) => state === 'Submitted' || state === 'Approved');
}

/** `422 rekeyKeyInvalid` — під полем ключа, а не банером: людина може одразу виправити. */
function isRekeyKeyInvalid(error: unknown): boolean {
  return (
    error instanceof EcrApiError
    && error.problem.errorCode === 'ECR-DOC-0422'
    && error.problem.extensions2?.['messageKey'] === 'err.ECR-DOC-0422.rekeyKeyInvalid'
  );
}

/** `422 rekeyReasonRequired` — під полем причини. */
function isRekeyReasonRequired(error: unknown): boolean {
  return (
    error instanceof EcrApiError
    && error.problem.errorCode === 'ECR-DOC-0422'
    && error.problem.extensions2?.['messageKey'] === 'err.ECR-DOC-0422.rekeyReasonRequired'
  );
}

/**
 * `409 rekeyStale`: чинний ключ на сервері вже не той, що бачила людина —
 * хтось змінив його першим.
 *
 * ⚠ Єдина відмова, після якої форму НЕ можна просто повторити: значення
 * `expectedBusinessKey`, з яким пішов запит, застаріле, а не невалідне.
 */
function isRekeyStale(error: unknown): boolean {
  return (
    error instanceof EcrApiError
    && error.problem.errorCode === 'ECR-DOC-0409'
    && error.problem.extensions2?.['messageKey'] === 'err.ECR-DOC-0409.rekeyStale'
  );
}

export interface BusinessKeyChangeActionArgs {
  readonly documentId: number;

  /** `undefined` — документ ще не приїхав: кнопки немає, немає й чинного ключа. */
  readonly document: DocumentSummary | undefined;

  /** Період — частина ключа кешу документа (R-A6), потрібен для інвалідизації. */
  readonly periodKey: number;
}

export interface BusinessKeyChangeAction {
  /**
   * Пункт меню «More» сторінки документа; `null`, якщо права немає.
   *
   * ⛔ Не кнопка в рядку дій: зміна ключа — рідкісна дія з аудитом, і серед
   * щоденних вона лише переповнювала рядок (знімок людини, 1290 px).
   */
  readonly menuItem: JSX.Element | null;

  /** Діалог — ОКРЕМО від пункту: меню розмонтовує вміст, щойно закривається. */
  readonly dialog: JSX.Element | null;

  /**
   * Банер відмови `rekeyStale` — під шапкою, ПІСЛЯ закриття діалогу.
   *
   * ⚠ Решта відмов (409 duplicate/locked, 422 reason/key) лишаються
   * УСЕРЕДИНІ діалогу — форму, яку можна виправити й надіслати знову,
   * закривати немає сенсу (той самий вибір, що в `security/UserAdminActions.tsx`
   * для скидання пароля: невдалий пароль лишає діалог відкритим). `rekeyStale`
   * інша: значення, з яким пішов запит, застаріле, а не невалідне, — форму
   * повторно надсилати нема чим, доки не прочитано новий ключ.
   */
  readonly refusal: JSX.Element | null;
}

/**
 * Дія «Змінити номер справи» (бізнес-ключ документа, ФВ-3.9) на сторінці
 * документа.
 *
 * ⛔ Право `Document.ChangeKey` — **нове й небезпечне**, жодна роль сіду його
 * не має (`09-seed.sql` цим пакетом не чіпається): кнопка з'являється лише
 * тоді, коли право видали явно, і разом із ним — грант Write на проєкт
 * (`hasProjectWriteGrant`), як того вимагає сервер.
 *
 * ⚠ Аркуші поданий/погоджений — кнопка вимкнена ЗАЗДАЛЕГІДЬ, з видимою
 * причиною поруч (той самий прийом, що `DataSourceDrawer.tsx` для
 * `sourceEntities > 0 || collectionSchedules > 0`): лічильники клієнта могли
 * застаріти, серверна відмова `409 rekeyLocked` лишається остаточною.
 */
export function useBusinessKeyChangeAction({
  documentId,
  document,
  periodKey,
}: BusinessKeyChangeActionArgs): BusinessKeyChangeAction {
  const session = useSession();
  const queryClient = useQueryClient();
  const me = session.data;

  const [opened, setOpened] = useState(false);
  const [newKey, setNewKey] = useState('');
  const [reason, setReason] = useState('');

  const lockedReasonId = useId();

  const allowed =
    can(me, ChangeDocumentKeyPermission)
    && document !== undefined
    && hasProjectWriteGrant(me, document.projectId);

  const locked = document !== undefined && hasLockedSheet(document.sheetStates);

  const change = useMutation({
    mutationFn: () => {
      // ⛔ Кнопка, що відкриває діалог, показується лише коли `document` уже
      // є (`allowed` вище), тож на момент підтвердження документ завжди
      // визначений. Явна відмова тут — страховка компілятора, не робочий шлях.
      if (document === undefined) {
        return Promise.reject(new Error('Документ ще не завантажено.'));
      }

      return changeDocumentBusinessKey({
        documentId,
        businessKey: newKey.trim(),
        expectedBusinessKey: document.businessKey,
        reason: reason.trim(),
      });
    },
    onSuccess: async () => {
      setOpened(false);

      // ⚠ Перелік документів теж показує бізнес-ключ (`DocumentsPage`) —
      // спільний префікс `['documents']` оновлює і його, і зведення над ним
      // (той самий прийом, що `useDocumentListSummary`).
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] }),
        queryClient.invalidateQueries({ queryKey: ['documents'] }),
      ]);

      showDone(t('documents.keyChanged'));
    },
    onError: async (error) => {
      // ⚠ ЛИШЕ `rekeyStale` закриває діалог і перечитує документ: чинний
      // ключ на сервері вже не той, з яким пішов запит, — надсилати ту саму
      // форму ще раз нема чим, доки людина не побачить новий ключ.
      if (isRekeyStale(error)) {
        setOpened(false);
        await queryClient.invalidateQueries({ queryKey: ['document', documentId, periodKey] });
      }
    },
  });

  const { reset: resetChange } = change;

  // ⚠ Поля очищаються при КОЖНОМУ відкритті — той самий прийом, що
  // `ReasonModal`/`TestDataSourceModal`: причина чи ключ, що лишилися з
  // попередньої спроби, тут особливо небезпечні, бо йдуть в аудит зміни
  // бізнес-ключа.
  useEffect(() => {
    if (opened) {
      setNewKey('');
      setReason('');
      resetChange();
    }
  }, [opened, resetChange]);

  const currentKey = document?.businessKey ?? '';
  const trimmedKey = newKey.trim();
  const trimmedReason = reason.trim();

  const keyValid =
    trimmedKey.length > 0 && trimmedKey.length <= BusinessKeyMaxLength && trimmedKey !== currentKey;
  const reasonValid = trimmedReason.length > 0;
  const canSubmit = keyValid && reasonValid && document !== undefined && !change.isPending;

  const shownProblem = change.error !== null ? problemText(change.error) : null;

  const keyServerError = isRekeyKeyInvalid(change.error)
    ? (shownProblem?.detail ?? shownProblem?.title ?? null)
    : null;

  const reasonServerError = isRekeyReasonRequired(change.error)
    ? (shownProblem?.detail ?? shownProblem?.title ?? null)
    : null;

  // Решта відмов (duplicate/locked, або щось геть інше — 403/404/500):
  // банер під полями, форма лишається на екрані.
  const dialogBannerError =
    change.error !== null && !isRekeyStale(change.error) && keyServerError === null && reasonServerError === null
      ? change.error
      : null;

  /*
   * ⚠ Заблокований пункт лишається ВИДИМИМ і пояснює причину під підписом —
   * той самий прийом, що й кнопка до переїзду в меню: сховане без пояснення
   * читалося б як «права немає», а причина тут — стан аркушів.
   */
  const menuItem = allowed ? (
    <Menu.Item
      disabled={locked}
      // ⚠ Ім'я пункту — лише підпис дії: причина блокування стоїть усередині
      // того самого `<button>` і без явного імені склеїлася б із ним. Причину
      // читач чує через `aria-describedby`.
      aria-label={t('documents.changeKey')}
      aria-describedby={locked ? lockedReasonId : undefined}
      onClick={() => setOpened(true)}
      data-change-business-key=""
    >
      {/* ⚠ Лише `span`-и: пункт меню — це `<button>`, і блоковий `div`/`p`
          усередині нього — невалідна вкладеність. */}
      <span style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        <span>{t('documents.changeKey')}</span>
        {locked && (
          <Text
            component="span"
            id={lockedReasonId}
            size="xs"
            c="dimmed"
            maw={260}
            data-change-key-blocked-reason=""
          >
            {t('documents.changeKeyLockedHint')}
          </Text>
        )}
      </span>
    </Menu.Item>
  ) : null;

  const dialog = allowed ? (
    <Modal
      opened={opened}
      onClose={() => setOpened(false)}
      title={t('documents.changeKeyTitle')}
    >
      <Stack gap="sm">
        <TextInput
          label={t('documents.newBusinessKey')}
          description={t('documents.newBusinessKeyHint', { max: BusinessKeyMaxLength })}
          value={newKey}
          onChange={(event) => setNewKey(event.currentTarget.value)}
          error={keyServerError}
          data-autofocus
        />

        <Textarea
          label={t('workflow.reason')}
          value={reason}
          onChange={(event) => setReason(event.currentTarget.value)}
          error={reasonServerError}
          minRows={3}
          autosize
        />

        {dialogBannerError !== null && <ErrorAlert error={dialogBannerError} />}

        <Group justify="flex-end" mt="xs">
          <Button variant="default" onClick={() => setOpened(false)} data-testid="business-key-cancel">
            {t('common.cancel')}
          </Button>
          <Button
            disabled={!canSubmit}
            loading={change.isPending}
            onClick={() => change.mutate()}
            data-testid="business-key-confirm"
          >
            {t('documents.changeKey')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  ) : null;

  const refusal =
    change.error !== null && isRekeyStale(change.error) ? (
      <Stack gap="xs" data-change-key-stale="">
        <Text size="sm" c="statusError">
          {t('documents.changeKeyStaleHint')}
        </Text>
        <ErrorAlert error={change.error} />
      </Stack>
    ) : null;

  return { menuItem, dialog, refusal };
}
