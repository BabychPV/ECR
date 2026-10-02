import { useEffect, useState, type JSX } from 'react';
import { Button, Group, List, Select, Skeleton, Stack, Text, Textarea, TextInput, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { RoleView } from '@/api/types';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import {
  getNotificationRules,
  listNotificationChannels,
  listUiStringsRaw,
  NotificationChannelsKey,
  NotificationRulesKey,
  notificationTemplateStringsKey,
  setNotificationTemplateString,
  type NotificationChannel,
  type NotificationRuleMatrix,
  type UiStringRawRow,
} from '@/features/notifications/api';
import {
  TemplatedEvents,
  TemplatePlaceholders,
  templateProblem,
  type TemplatedEvent,
  type TemplateProblem,
} from '@/features/notifications/notificationTemplates';
import { DefaultLanguage, t } from '@/shared/i18n';
import { useLanguages } from '@/shared/i18n/useLanguages';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showApiError, showDone } from '@/shared/ui/notify';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';

/** Право редактора каталогу рядків: шаблони листів — це рядки каталогу. */
const ManageLocalization = 'System.ManageLocalization';

/** Чернетка шаблону однієї мови. */
interface TemplateDraft {
  readonly subject: string;
  readonly body: string;
}

/**
 * Шаблони повідомлень (`CL-6`, `D-256`, `ФВ-12.4a`): «за якої події ЩО відправляти і КОМУ».
 *
 * ⛔ Окремого сховища шаблонів немає й не вигадується: тема й тіло листа — рядки каталогу
 * (`notifications.periodOpened.subject` …), які сервер бере за ключем і заповнює
 * `{project}`/`{period}` мовою кожного адресата (`PeriodStateJob.NotifyAsync`, `NotificationText`).
 * Тому читання — сирий перелік мови (`GET /ui-strings?lang=`, без підміни англійським), а запис —
 * `PUT /ui-strings/{lang}/{key}`, той самий, що й у редакторі рядків.
 *
 * ⚠ «Хто отримає» — вивід із матриці правил і каналів, а не окреме налаштування: адресати каналу —
 * ролі (`D-256`, без іменних осіб), правило вирішує, чи йде подія цим каналом. Події періоду мають
 * серйозність `Info`, тож правило з вищою межею їх НЕ пропускає — і це сказано словами.
 *
 * ⛔ Порядок станів — відмова → очікування → порожньо → дані (директива №15, `L10`).
 */
export function NotificationTemplatesPanel(): JSX.Element {
  const session = useSession();
  const allowed = can(session.data, ManageLocalization);

  const title = (
    <Title order={2} size="h5">
      {t('notificationTemplates.title')}
    </Title>
  );

  return (
    <Stack gap="sm">
      {title}
      <Text size="sm" c="dimmed">
        {t('notificationTemplates.hint')}
      </Text>
      {allowed ? (
        <TemplatesEditor />
      ) : (
        <Text size="sm" data-notification-templates="forbidden">
          {t('notificationTemplates.noPermission')}
        </Text>
      )}
    </Stack>
  );
}

function TemplatesEditor(): JSX.Element {
  const queryClient = useQueryClient();
  const languages = useLanguages();

  const [eventKind, setEventKind] = useState<TemplatedEvent['eventKind']>(TemplatedEvents[0].eventKind);
  const [lang, setLang] = useState(DefaultLanguage);
  const [draft, setDraft] = useState<TemplateDraft | null>(null);

  const strings = useQuery({
    queryKey: notificationTemplateStringsKey(lang),
    queryFn: () => listUiStringsRaw(lang),
  });

  const template = TemplatedEvents.find((item) => item.eventKind === eventKind) ?? TemplatedEvents[0];
  const isDefault = lang === DefaultLanguage || languages.data?.find((l) => l.code === lang)?.isDefault === true;

  const dirty = draft !== null;

  useEffect(() => {
    if (!dirty) return undefined;

    return registerUnsavedSource('notification-templates', {
      hasUnsaved: () => true,
      unsavedCount: () => 1,
    });
  }, [dirty]);

  const save = useMutation({
    mutationFn: async (next: { subject: string | undefined; body: string | undefined }) => {
      // ⚠ Послідовно, а не паралельно: дві правки одного каталогу — дві версії `Revision`; порядок
      // не важливий, але друга відмова (`422`) мусить прийти ПІСЛЯ першого успіху, щоб екран не
      // показав «збережено» на половину.
      if (next.subject !== undefined) await setNotificationTemplateString(lang, template.subjectKey, next.subject);
      if (next.body !== undefined) await setNotificationTemplateString(lang, template.bodyKey, next.body);
    },
    onSuccess: async () => {
      setDraft(null);
      await queryClient.invalidateQueries({ queryKey: ['ui-strings'] });
      showDone(t('notificationTemplates.saved'));
    },
    onError: async (error: unknown) => {
      // Перша половина могла вже записатися — перечитуємо, щоб екран не брехав про базу.
      await queryClient.invalidateQueries({
        queryKey: notificationTemplateStringsKey(lang),
      });
      showApiError(error);
    },
  });
  const saveLoading = usePendingLoading(save.isPending);

  const failure = languages.error ?? strings.error;
  if (failure !== null) {
    return (
      <ErrorAlert
        error={failure}
        onRetry={() => {
          void languages.refetch();
          void strings.refetch();
        }}
      />
    );
  }

  if (languages.isPending || strings.isPending || languages.data === undefined || strings.data === undefined) {
    return (
      <Stack gap="xs" aria-busy="true" data-notification-templates="pending">
        <Skeleton height={28} />
        <Skeleton height={28} />
        <Skeleton height={80} />
      </Stack>
    );
  }

  if (languages.data.length === 0) {
    return (
      <Text size="sm" data-notification-templates="no-languages">
        {t('notificationTemplates.noLanguages')}
      </Text>
    );
  }

  const subjectRow = strings.data.items.find((row) => row.key === template.subjectKey);
  const bodyRow = strings.data.items.find((row) => row.key === template.bodyKey);

  const selectors = (
    <Group align="flex-end" gap="sm">
      <Select
        label={t('notificationTemplates.event')}
        allowDeselect={false}
        disabled={dirty}
        data={TemplatedEvents.map((item) => ({
          value: item.eventKind,
          label: eventLabel(item.eventKind),
        }))}
        value={eventKind}
        onChange={(value) => {
          if (value !== null) setEventKind(value as TemplatedEvent['eventKind']);
        }}
      />
      <Select
        label={t('notificationTemplates.language')}
        allowDeselect={false}
        disabled={dirty}
        data={languages.data.map((language) => ({
          value: language.code,
          label: language.nameNative,
        }))}
        value={lang}
        onChange={(value) => {
          if (value !== null) setLang(value);
        }}
      />
    </Group>
  );

  // ⚠ Ключа немає в каталозі — сервер новіший/старіший за сід. Порожня форма тут записала б рядок,
  // якого сервер не читає, тож форми немає, а причина названа.
  if (subjectRow === undefined || bodyRow === undefined) {
    return (
      <Stack gap="sm">
        {selectors}
        <Text size="sm" data-notification-templates="not-in-catalog">
          {t('notificationTemplates.notInCatalog')}
        </Text>
      </Stack>
    );
  }

  const stored: TemplateDraft = {
    subject: valueOf(subjectRow, isDefault),
    body: valueOf(bodyRow, isDefault),
  };
  const form = draft ?? stored;
  const subjectProblem = templateProblem(form.subject, subjectRow.reference, isDefault);
  const bodyProblem = templateProblem(form.body, bodyRow.reference, isDefault);
  const untranslated = !isDefault && (subjectRow.value === null || bodyRow.value === null);
  const placeholders = TemplatePlaceholders.map((name) => `{${name}}`).join(', ');

  return (
    <Stack gap="sm">
      {selectors}

      <Recipients eventKind={eventKind} />

      {untranslated && (
        <Text size="sm" c="dimmed" data-notification-templates="untranslated">
          {t('notificationTemplates.untranslated')}
        </Text>
      )}

      <TextInput
        label={t('notificationTemplates.subject')}
        description={
          isDefault
            ? undefined
            : t('notificationTemplates.reference', {
                text: subjectRow.reference,
              })
        }
        value={form.subject}
        error={problemText(subjectProblem, template.subjectKey)}
        onChange={(event) => setDraft({ ...form, subject: event.currentTarget.value })}
      />
      <Textarea
        label={t('notificationTemplates.body')}
        description={isDefault ? undefined : t('notificationTemplates.reference', { text: bodyRow.reference })}
        autosize
        minRows={3}
        value={form.body}
        error={problemText(bodyProblem, template.bodyKey)}
        onChange={(event) => setDraft({ ...form, body: event.currentTarget.value })}
      />
      <Text size="xs" c="dimmed">
        {t('notificationTemplates.placeholders', { names: placeholders })}
      </Text>

      <Group gap="xs">
        <Button
          loading={saveLoading}
          disabled={!dirty || subjectProblem !== null || bodyProblem !== null}
          onClick={() => {
            if (save.isPending) return;
            save.mutate({
              subject: form.subject === stored.subject ? undefined : form.subject,
              body: form.body === stored.body ? undefined : form.body,
            });
          }}
        >
          {t('notificationTemplates.save')}
        </Button>
        <Button variant="default" disabled={!dirty || save.isPending} onClick={() => setDraft(null)}>
          {t('common.cancel')}
        </Button>
        {dirty && (
          <Text size="xs" c="dimmed" fs="italic" data-testid="notification-templates-unsaved">
            {t('notificationTemplates.unsaved')}
          </Text>
        )}
      </Group>
    </Stack>
  );
}

/**
 * Хто отримає лист цієї події: увімкнені правила матриці × канали × ролі-адресати каналу.
 *
 * ⚠ Відмова тут не валить редактор: текст шаблону правиться й без матриці, а «невідомо кому» —
 * сказано словами з повтором, а не порожнім переліком (порожньо ≠ невідомо).
 */
function Recipients({ eventKind }: { readonly eventKind: TemplatedEvent['eventKind'] }): JSX.Element {
  const session = useSession();
  const rules = useQuery<NotificationRuleMatrix>({
    queryKey: NotificationRulesKey,
    queryFn: getNotificationRules,
  });
  const channels = useQuery<NotificationChannel[]>({
    queryKey: NotificationChannelsKey,
    queryFn: listNotificationChannels,
  });
  const canListRoles = can(session.data, 'Security.ManageRoles');
  const roles = useQuery({
    queryKey: ['roles'],
    queryFn: () => apiFetch<RoleView[]>('/api/v1/roles'),
    enabled: canListRoles,
  });

  const heading = (
    <Text size="sm" fw={600}>
      {t('notificationTemplates.recipients')}
    </Text>
  );

  const failure = rules.error ?? channels.error;
  if (failure !== null) {
    return (
      <Stack gap="xs" data-notification-recipients="error">
        {heading}
        <ErrorAlert
          error={failure}
          onRetry={() => {
            void rules.refetch();
            void channels.refetch();
          }}
        />
      </Stack>
    );
  }

  if (rules.data === undefined || channels.data === undefined) {
    return (
      <Stack gap="xs" aria-busy="true" data-notification-recipients="pending">
        {heading}
        <Skeleton height={20} />
      </Stack>
    );
  }

  const byId = new Map(channels.data.map((channel) => [channel.id, channel]));
  const roleCode = new Map((roles.data ?? []).map((role) => [role.id, role.code]));
  const enabled = rules.data.rules.filter(
    (rule) => rule.eventKind === eventKind && rule.isEnabled && byId.has(rule.channelId),
  );

  if (enabled.length === 0) {
    return (
      <Stack gap="xs" data-notification-recipients="none">
        {heading}
        <Text size="sm">{t('notificationTemplates.recipientsNone')}</Text>
      </Stack>
    );
  }

  return (
    <Stack gap="xs" data-notification-recipients="list">
      {heading}
      <List size="sm" spacing="xs">
        {enabled.map((rule) => {
          const channel = byId.get(rule.channelId) as NotificationChannel;
          const roleIds = channel.settings.recipientRoleIds ?? [];
          const notes: string[] = [];

          if (roleIds.length === 0) {
            notes.push(t('notificationTemplates.recipientsNoRoles'));
          } else if (roles.data !== undefined) {
            notes.push(
              t('notificationTemplates.recipientsRoles', {
                roles: roleIds.map((id) => roleCode.get(id) ?? `#${String(id)}`).join(', '),
              }),
            );
          } else {
            notes.push(
              t('notificationTemplates.recipientsRoleCount', {
                count: roleIds.length,
              }),
            );
          }

          // Події періоду — `Info`; межа вище пропускає лише гірші події, тобто цю — ні.
          if (rule.minSeverity !== 'Info') notes.push(t('notificationTemplates.recipientsFiltered'));
          if (!channel.transportConfigured) notes.push(t('notificationTemplates.recipientsNoTransport'));

          return (
            <List.Item key={rule.channelId} data-channel-id={rule.channelId}>
              {channel.name} — {notes.join('; ')}
            </List.Item>
          );
        })}
      </List>
    </Stack>
  );
}

/** Значення поля: у мові перекладу відсутнє — порожньо (не англійська підміна). */
function valueOf(row: UiStringRawRow, isDefault: boolean): string {
  if (row.value !== null) return row.value;

  return isDefault ? row.reference : '';
}

/** Підпис події — літерали, щоб сторож ключів бачив кожен рядок. */
function eventLabel(kind: TemplatedEvent['eventKind']): string {
  if (kind === 'PeriodGraceStarted') return t('notifications.event.PeriodGraceStarted');

  return t('notifications.event.PeriodOpened');
}

function problemText(problem: TemplateProblem | null, key: string): string | undefined {
  if (problem === null) return undefined;
  if (problem.kind === 'empty') return t('notificationTemplates.problemEmpty');
  if (problem.kind === 'unknownPlaceholder') {
    return t('notificationTemplates.problemUnknownPlaceholder', {
      names: problem.names.map((name) => `{${name}}`).join(', '),
      allowed: TemplatePlaceholders.map((name) => `{${name}}`).join(', '),
    });
  }

  return t('err.ECR-REQ-0422.placeholderMismatch', {
    key,
    expected: problem.expected.join(', '),
    actual: problem.actual.join(', '),
  });
}
