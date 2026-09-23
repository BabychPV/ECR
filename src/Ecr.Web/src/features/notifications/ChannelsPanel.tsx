import { useState, type JSX } from 'react';
import {
  Badge,
  Button,
  Group,
  Modal,
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
import { Hint } from '@/shared/ui/Hint';
import { Timestamp } from '@/shared/ui/Timestamp';
import { showApiError, showDone } from '@/shared/ui/notify';
import {
  createNotificationChannel,
  deleteNotificationChannel,
  listNotificationChannels,
  NotificationChannelsKey,
  replaceNotificationChannelSecret,
  testNotificationChannel,
  updateNotificationChannel,
  type NotificationChannel,
  type NotificationChannelSettings,
} from './api';
import { TransportSource } from './TransportSource';

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
    queryKey: NotificationChannelsKey,
    queryFn: listNotificationChannels,
  });

  /*
   * ⛔ Ключ — `NotificationChannelsKey` з `api.ts`, СПІЛЬНИЙ із
   * `RulesMatrixPanel`: інвалідація за власним ключем цього компонента не
   * зачепила б кеш іншого споживача тих самих даних, і той показував би старе
   * до випадкового ремаунту (був дефект — див. коментар біля константи).
   */
  const invalidate = async (): Promise<void> => {
    await queryClient.invalidateQueries({ queryKey: NotificationChannelsKey });
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
        <Stack gap="xs">
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
              <Table.Th>{t('notifications.webhookUrl')}</Table.Th>
              <Table.Th>{t('notifications.modified')}</Table.Th>
              <Table.Th />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {channels.data.map((channel) => (
              <Table.Tr key={channel.id}>
                <Table.Td>{channel.name}</Table.Td>
                <Table.Td>
                  <Stack gap="xs" align="flex-start">
                    <Text size="sm">{t(`notifications.kind.${channel.kind}`)}</Text>
                    <TransportSource channel={channel} />
                  </Stack>
                </Table.Td>
                <Table.Td>
                  {channel.isEnabled ? t('notifications.enabledYes') : t('notifications.enabledNo')}
                </Table.Td>
                <Table.Td>
                  {/*
                    ⛔ Секрет каналу читає РІВНО ОДИН відправник — `TeamsWebhookSender`,
                    і для нього це сама адреса вебхука (без неї він відмовляє:
                    «адресу вебхука не задано»). `SmtpChannelSender` секрету
                    каналу не торкається взагалі — пароль бере транспорт процесу.
                    Тому для пошти тут не «немає секрету» (це читалося б як
                    незавершене налаштування), а «не застосовується».

                    ⚠ Для Teams навпаки: відсутня адреса — саме попередження.
                    Канал ввімкнений і не доставить нічого, а дізнаються про це
                    тоді, коли сповіщення були потрібні.
                  */}
                  {channel.kind !== 'TeamsWebhook' ? (
                    <Hint label={t('notifications.secretNotUsedSmtp')} focusable>
                      <Text size="sm" c="dimmed">
                        {t('notifications.notApplicable')}
                      </Text>
                    </Hint>
                  ) : channel.hasSecret ? (
                    <Text size="sm">{t('notifications.webhookSet')}</Text>
                  ) : (
                    <Badge color="statusWarning" variant="light">
                      {t('notifications.webhookMissing')}
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
                    {/* ⚠ Дія є лише там, де секрет справді читають: для пошти
                        збережене значення нікуди не піде, а кнопка обіцяла б
                        налаштування. */}
                    {channel.kind === 'TeamsWebhook' && (
                      <Button
                        size="compact-xs"
                        variant="subtle"
                        onClick={() => {
                          setSecretFor(channel);
                          setSecret('');
                        }}
                      >
                        {t('notifications.setWebhook')}
                      </Button>
                    )}
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

            {/*
              ⛔ Форма показує РІВНО ті поля, які сервер справді читає, і це
              перевірено в коді відправників, а не за назвами в контракті:
              `SmtpChannelSender` бере з каналу `settings.Recipients` і
              `settings.Title` (префікс теми), а транспорт — сервер, відправника
              й пароль — із конфігурації ПРОЦЕСУ (`Smtp:Host`, `Smtp:From`).
              Полів `host`/`port`/`from`/`useTls` немає ні тут, ні в контракті
              видачі, а у вхідному вони дають `422` (`c3652cee`).

              ⚠ Звідки транспорт — каже сервер (`transportFromConfiguration`),
              коли канал уже є. Для нового каналу відповіді ще немає, тож там
              лишається статична підказка: вигадати ознаку наперед означало б
              сказати за сервер.
            */}
            {draft.kind === 'Smtp' && (
              <>
                {draft.transportFromConfiguration === null || draft.transportConfigured === null ? (
                  <Text size="sm" c="dimmed">
                    {t('notifications.smtpTransportHint')}
                  </Text>
                ) : (
                  <TransportSource
                    channel={{
                      kind: draft.kind,
                      transportFromConfiguration: draft.transportFromConfiguration,
                      transportConfigured: draft.transportConfigured,
                    }}
                  />
                )}

                <TextInput
                  label={t('notifications.smtpRecipients')}
                  description={t('notifications.smtpRecipientsHint')}
                  value={draft.recipients}
                  onChange={(event) => setDraft({ ...draft, recipients: event.currentTarget.value })}
                />
              </>
            )}

            {/* ⚠ Поле одне (`settings.title`), а значить різне: у листі це
                ПРЕФІКС теми (`SmtpChannelSender.SubjectOf`), у Teams —
                заголовок картки (`TeamsWebhookSender.TitleOf`). Тому підпис і
                підказка залежать від транспорту. */}
            <TextInput
              label={
                draft.kind === 'Smtp'
                  ? t('notifications.subjectPrefix')
                  : t('notifications.teamsTitle')
              }
              description={
                draft.kind === 'Smtp'
                  ? t('notifications.subjectPrefixHint')
                  : t('notifications.teamsTitleHint')
              }
              value={draft.title}
              onChange={(event) => setDraft({ ...draft, title: event.currentTarget.value })}
            />

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
        title={t('notifications.setWebhook')}
      >
        {secretFor !== null && (
          <Stack gap="sm">
            {/* ⛔ Чинного значення тут не показано й показати нічим: сервер
                адреси не віддає (лише ознаку `hasSecret`). Тому підпис
                говорить про ЗАМІНУ, а не про правку.

                ⚠ Названо й межу сервера: хост вебхука перевіряється переліком
                дозволених суфіксів, і недозволений дає `422 ECR-REQ-0422` —
                дізнатися про це з мовчазної відмови було б дорожче. */}
            <Text size="sm" c="dimmed">
              {t('notifications.webhookHint')}
            </Text>

            <TextInput
              label={t('notifications.webhookUrl')}
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
                {t('notifications.clearWebhook')}
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

/**
 * Чернетка каналу у формі: рядки, бо форма працює з текстом, а не з `null`.
 *
 * ⚠ Полів `host`/`port`/`from`/`useTls` тут НЕМАЄ навмисно: сервер відхиляє
 * їх `422 ECR-REQ-0422` (`notificationChannelTransportFromConfiguration`).
 *
 * ⚠ `transportFromConfiguration` і `transportConfigured` — ознаки з ВІДПОВІДІ
 * сервера, на сервер вони не їдуть; `null` — канал ще не створено, і сервер
 * про нього нічого не казав.
 */
interface ChannelDraft {
  readonly id: number | null;
  readonly name: string;
  readonly kind: 'Smtp' | 'TeamsWebhook';
  readonly isEnabled: boolean;
  readonly recipients: string;
  readonly title: string;
  readonly transportFromConfiguration: boolean | null;
  readonly transportConfigured: boolean | null;
}

function emptyDraft(): ChannelDraft {
  return {
    id: null,
    name: '',
    kind: 'Smtp',
    isEnabled: true,
    recipients: '',
    title: '',
    transportFromConfiguration: null,
    transportConfigured: null,
  };
}

function draftOf(channel: NotificationChannel): ChannelDraft {
  return {
    id: channel.id,
    name: channel.name,
    kind: channel.kind,
    isEnabled: channel.isEnabled,
    recipients: (channel.settings.recipients ?? []).join(', '),
    title: channel.settings.title ?? '',
    transportFromConfiguration: channel.transportFromConfiguration,
    transportConfigured: channel.transportConfigured,
  };
}

/**
 * Чернетка → несекретні параметри.
 *
 * ⚠ Порожній рядок їде як `null`, а не як `''`: `''` у налаштуваннях означав
 * би «задано порожнім», і сервер зберіг би саме це.
 *
 * ⛔ Тип повернення — `NotificationChannelSettings` (видача: лише `recipients`
 * і `title`), а не ширший `NotificationChannelSettingsInput`: так `host`,
 * доданий сюди, не скомпілюється, а не поїде на сервер по `422`.
 */
function settingsOf(draft: ChannelDraft): NotificationChannelSettings {
  if (draft.kind === 'Smtp') {
    return {
      recipients: draft.recipients
        .split(',')
        .map((one) => one.trim())
        .filter((one) => one.length > 0),
      title: blankToNull(draft.title),
    };
  }

  return { title: blankToNull(draft.title) };
}

function blankToNull(value: string): string | null {
  return value.trim().length === 0 ? null : value.trim();
}
