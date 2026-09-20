import { useState, type JSX } from 'react';
import {
  Badge,
  Button,
  Group,
  Modal,
  NumberInput,
  Select,
  Skeleton,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Title,
} from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { Timestamp } from '@/shared/ui/Timestamp';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  createNotificationChannel,
  deleteNotificationChannel,
  listNotificationChannels,
  replaceNotificationChannelSecret,
  testNotificationChannel,
  updateNotificationChannel,
  type NotificationChannel,
  type NotificationChannelSettings,
} from './api';

/**
 * Канали сповіщень (`BE-33`, екран `/admin/notifications`).
 *
 * ⛔ Секрет каналу сюди не приходить НІКОЛИ — сервер віддає лише `hasSecret`
 * (`NotificationChannelView`). Тому й поле секрету тут не «редагування
 * значення», а ЗАМІНА: показати нічого, записати нове, або прибрати.
 *
 * ⚠ Канал без секрету показано попередженням, а не мовчазним порожнім місцем:
 * такий канал існує, ввімкнений і НЕ доставить нічого — а це рівно той стан,
 * про який дізнаються в момент, коли сповіщення були потрібні.
 */
export function ChannelsPanel(): JSX.Element {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<ChannelDraft | null>(null);
  const [secretFor, setSecretFor] = useState<NotificationChannel | null>(null);
  const [secret, setSecret] = useState('');

  const channels = useQuery({
    queryKey: ['notification-channels'],
    queryFn: listNotificationChannels,
  });

  const invalidate = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: ['notification-channels'] });
  };

  const save = useMutation({
    mutationFn: (value: ChannelDraft) =>
      value.id === null
        ? createNotificationChannel({
            name: value.name,
            kind: value.kind,
            settings: settingsOf(value),
          })
        : updateNotificationChannel(value.id, {
            name: value.name,
            isEnabled: value.isEnabled,
            settings: settingsOf(value),
          }),
    onSuccess: async () => {
      await invalidate();
      setDraft(null);
      showDone(t('notifications.channelSaved'));
    },
    onError: showApiError,
  });

  const remove = useMutation({
    mutationFn: (id: number) => deleteNotificationChannel(id),
    onSuccess: async () => {
      await invalidate();
      showDone(t('notifications.channelDeleted'));
    },
    onError: showApiError,
  });

  const saveSecret = useMutation({
    mutationFn: (value: { id: number; secret: string | null }) =>
      replaceNotificationChannelSecret(value.id, value.secret),
    onSuccess: async () => {
      await invalidate();
      setSecretFor(null);
      setSecret('');
      showDone(t('notifications.secretSaved'));
    },
    onError: showApiError,
  });

  const test = useMutation({
    mutationFn: (id: number) => testNotificationChannel(id),

    /*
     * ⛔ `ok: false` — це ВІДПОВІДЬ каналу, а не помилка запиту (сервер віддає
     * `200` з причиною всередині), тож вона й показується як відповідь.
     * Причину беремо з каталогу, коли сервер назвав `messageKey`, і сирим
     * текстом сервера — коли ні: власного «не вдалося» тут бути не може
     * (`ФВ-14.24`).
     */
    onSuccess: (result) => {
      if (result.ok) {
        showDone(t('notifications.testOk'));
        return;
      }

      const key = result.messageKey ?? null;
      showApiError(new Error(key === null ? (result.error ?? t('notifications.testFailed')) : t(key)));
    },
    onError: showApiError,
  });

  return (
    <Stack gap="sm">
      <Group justify="space-between" align="center">
        <Title order={2} size="h5">
          {t('notifications.channels')}
        </Title>

        <Button size="xs" variant="default" onClick={() => setDraft(emptyDraft())}>
          {t('notifications.addChannel')}
        </Button>
      </Group>

      {/*
        ⛔ Директива D15 §0, правило L10: відмова `GET …/channels` НЕ робить
        таблицю порожньою. Порожній перелік каналів читається як «сповіщення не
        налаштовані», і адміністратор заводить канал, який уже є, — а на
        сусідній вкладці та сама відмова ще й забирає вісь матриці правил.
      */}
      {channels.error !== null && (
        <ErrorAlert error={channels.error} onRetry={() => void channels.refetch()} />
      )}

      {channels.error === null && channels.isPending && (
        <Skeleton height={120} radius="sm" data-channels="pending" />
      )}

      {channels.error === null && !channels.isPending && channels.data.length === 0 && (
        <Stack gap={4}>
          <Text c="dimmed">{t('notifications.noChannels')}</Text>
          <Text size="sm" c="dimmed">
            {t('notifications.noChannelsHint')}
          </Text>
        </Stack>
      )}

      {channels.error === null && !channels.isPending && channels.data.length > 0 && (
        <Table striped highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('notifications.channelName')}</Table.Th>
              <Table.Th>{t('notifications.channelKind')}</Table.Th>
              <Table.Th>{t('notifications.enabled')}</Table.Th>
              <Table.Th>{t('notifications.secret')}</Table.Th>
              <Table.Th>{t('notifications.modified')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {channels.data.map((channel) => (
              <Table.Tr key={channel.id}>
                <Table.Td>{channel.name}</Table.Td>
                <Table.Td>{t(`notifications.kind.${channel.kind}`)}</Table.Td>
                <Table.Td>
                  {channel.isEnabled ? t('notifications.enabledYes') : t('notifications.enabledNo')}
                </Table.Td>
                <Table.Td>
                  {/* ⚠ Канал без секрету — попередження, а не порожня комірка:
                      він ввімкнений і не доставить нічого. */}
                  {channel.hasSecret ? (
                    <Text size="sm">{t('notifications.secretSet')}</Text>
                  ) : (
                    <Badge color="statusWarning" variant="light">
                      {t('notifications.secretMissing')}
                    </Badge>
                  )}
                </Table.Td>
                <Table.Td>
                  <Timestamp value={channel.modifiedAt} />
                </Table.Td>
                <Table.Td>
                  <Group gap="xs" justify="flex-end">
                    {/* ⚠ `notifications.editChannel`, а не спільний
                        `common.edit`: такого ключа в каталозі немає, і
                        заводити спільний рядок заради одного екрана означало б
                        вирішувати за всі інші, як у них зветься ця дія. */}
                    <Button size="compact-xs" variant="subtle" onClick={() => setDraft(draftOf(channel))}>
                      {t('notifications.editChannel')}
                    </Button>
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      onClick={() => {
                        setSecretFor(channel);
                        setSecret('');
                      }}
                    >
                      {t('notifications.setSecret')}
                    </Button>
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      loading={test.isPending && test.variables === channel.id}
                      onClick={() => test.mutate(channel.id)}
                    >
                      {t('notifications.testChannel')}
                    </Button>
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      color="statusError"
                      onClick={() => remove.mutate(channel.id)}
                    >
                      {t('common.delete')}
                    </Button>
                  </Group>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      )}

      <Modal
        opened={draft !== null}
        onClose={() => setDraft(null)}
        title={t('notifications.channelForm')}
      >
        {draft !== null && (
          <Stack gap="sm">
            <TextInput
              label={t('notifications.channelName')}
              value={draft.name}
              data-autofocus
              onChange={(event) => setDraft({ ...draft, name: event.currentTarget.value })}
            />

            {/* ⚠ Транспорт задається лише при створенні: сервер його не міняє
                (`UpdateNotificationChannelRequest` цього поля не має), і
                показаний вибір обіцяв би те, чого не станеться. */}
            <Select
              label={t('notifications.channelKind')}
              data={[
                { value: 'Smtp', label: t('notifications.kind.Smtp') },
                { value: 'TeamsWebhook', label: t('notifications.kind.TeamsWebhook') },
              ]}
              value={draft.kind}
              disabled={draft.id !== null}
              allowDeselect={false}
              onChange={(value) => {
                if (value !== null) setDraft({ ...draft, kind: value as ChannelDraft['kind'] });
              }}
            />

            {draft.id !== null && (
              <Switch
                label={t('notifications.enabled')}
                checked={draft.isEnabled}
                onChange={(event) => setDraft({ ...draft, isEnabled: event.currentTarget.checked })}
              />
            )}

            {draft.kind === 'Smtp' ? (
              <>
                <TextInput
                  label={t('notifications.smtpHost')}
                  value={draft.host}
                  onChange={(event) => setDraft({ ...draft, host: event.currentTarget.value })}
                />
                <NumberInput
                  label={t('notifications.smtpPort')}
                  value={draft.port ?? ''}
                  min={1}
                  onChange={(value) =>
                    setDraft({ ...draft, port: typeof value === 'number' ? value : null })
                  }
                />
                <TextInput
                  label={t('notifications.smtpFrom')}
                  value={draft.from}
                  onChange={(event) => setDraft({ ...draft, from: event.currentTarget.value })}
                />
                <TextInput
                  label={t('notifications.smtpRecipients')}
                  description={t('notifications.smtpRecipientsHint')}
                  value={draft.recipients}
                  onChange={(event) => setDraft({ ...draft, recipients: event.currentTarget.value })}
                />
                <Switch
                  label={t('notifications.smtpUseTls')}
                  checked={draft.useTls}
                  onChange={(event) => setDraft({ ...draft, useTls: event.currentTarget.checked })}
                />
              </>
            ) : (
              <TextInput
                label={t('notifications.teamsTitle')}
                description={t('notifications.teamsTitleHint')}
                value={draft.title}
                onChange={(event) => setDraft({ ...draft, title: event.currentTarget.value })}
              />
            )}

            <Group justify="flex-end">
              <Button variant="default" onClick={() => setDraft(null)}>
                {t('common.cancel')}
              </Button>
              <Button
                loading={save.isPending}
                disabled={draft.name.trim().length === 0}
                onClick={() => save.mutate(draft)}
              >
                {t('common.save')}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>

      <Modal
        opened={secretFor !== null}
        onClose={() => setSecretFor(null)}
        title={t('notifications.setSecret')}
      >
        {secretFor !== null && (
          <Stack gap="sm">
            {/* ⛔ Чинного значення тут не показано й показати нічим: сервер
                секрет не віддає. Тому підпис говорить про ЗАМІНУ, а не про
                правку. */}
            <Text size="sm" c="dimmed">
              {t('notifications.secretHint')}
            </Text>

            <TextInput
              label={t('notifications.secret')}
              type="password"
              value={secret}
              data-autofocus
              onChange={(event) => setSecret(event.currentTarget.value)}
            />

            <Group justify="flex-end">
              <Button
                variant="default"
                color="statusError"
                loading={saveSecret.isPending}
                disabled={!secretFor.hasSecret}
                onClick={() => saveSecret.mutate({ id: secretFor.id, secret: null })}
              >
                {t('notifications.clearSecret')}
              </Button>
              <Button
                loading={saveSecret.isPending}
                disabled={secret.trim().length === 0}
                onClick={() => saveSecret.mutate({ id: secretFor.id, secret })}
              >
                {t('common.save')}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
    </Stack>
  );
}

/** Чернетка каналу у формі: рядки, бо форма працює з текстом, а не з `null`. */
interface ChannelDraft {
  readonly id: number | null;
  readonly name: string;
  readonly kind: 'Smtp' | 'TeamsWebhook';
  readonly isEnabled: boolean;
  readonly host: string;
  readonly port: number | null;
  readonly from: string;
  readonly recipients: string;
  readonly useTls: boolean;
  readonly title: string;
}

function emptyDraft(): ChannelDraft {
  return {
    id: null,
    name: '',
    kind: 'Smtp',
    isEnabled: true,
    host: '',
    port: null,
    from: '',
    recipients: '',
    useTls: true,
    title: '',
  };
}

function draftOf(channel: NotificationChannel): ChannelDraft {
  return {
    id: channel.id,
    name: channel.name,
    kind: channel.kind,
    isEnabled: channel.isEnabled,
    host: channel.settings.host ?? '',
    port: channel.settings.port ?? null,
    from: channel.settings.from ?? '',
    recipients: (channel.settings.recipients ?? []).join(', '),
    useTls: channel.settings.useTls ?? true,
    title: channel.settings.title ?? '',
  };
}

/**
 * Чернетка → несекретні параметри.
 *
 * ⚠ Порожній рядок їде як `null`, а не як `''`: `''` у налаштуваннях означав
 * би «задано порожнім», і сервер зберіг би саме це.
 */
function settingsOf(draft: ChannelDraft): NotificationChannelSettings {
  if (draft.kind === 'Smtp') {
    return {
      host: blankToNull(draft.host),
      port: draft.port,
      from: blankToNull(draft.from),
      recipients: draft.recipients
        .split(',')
        .map((one) => one.trim())
        .filter((one) => one.length > 0),
      useTls: draft.useTls,
    };
  }

  return { title: blankToNull(draft.title) };
}

function blankToNull(value: string): string | null {
  return value.trim().length === 0 ? null : value.trim();
}
