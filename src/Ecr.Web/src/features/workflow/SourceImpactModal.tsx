import { useEffect, useState, type JSX } from 'react';
import { Alert, Badge, Button, Group, List, Modal, Stack, Text, Textarea } from '@mantine/core';
import { t } from '@/shared/i18n';
import { formatPeriodKey } from '@/shared/format';
import { useRestoreFocus } from '@/shared/ui/useRestoreFocus';
import type { ConfirmedImpact, SourceImpact, SourceImpactAction } from './sourceImpact';

/**
 * B80 N5 (D-554): діалог впливу затвердження / повернення аркуша-джерела на залежні документи.
 *
 * Показує, які залежні документи й періоди зачепить дія і які з них уже затверджено (затверджений період не
 * перераховується — він застаріє), а далі за політикою зв'язки: `Warn` — підтвердження; `ConfirmWithReason` —
 * підтвердження з причиною (іде в журнал безпеки); `Block` — пояснення відмови без кнопки підтвердження.
 *
 * ⛔ Фокус на «Cancel» (правило `L6`): `Enter` не виконує дію, поки людина не прочитала вплив. Підтвердження несе
 * відбиток впливу, який бачила людина, — сервер відмовить, якщо вплив змінився.
 */
export function SourceImpactModal({
  opened,
  action,
  impact,
  isPending,
  onConfirm,
  onClose,
}: {
  readonly opened: boolean;
  readonly action: SourceImpactAction;
  readonly impact: SourceImpact | null;
  readonly isPending?: boolean | undefined;
  readonly onConfirm: (confirmed: ConfirmedImpact) => void;
  readonly onClose: () => void;
}): JSX.Element | null {
  const [reason, setReason] = useState('');
  useRestoreFocus(opened);

  // ⚠ Причина очищається при КОЖНОМУ відкритті: причина попередньої дії не підписує наступну.
  useEffect(() => {
    if (opened) setReason('');
  }, [opened]);

  if (impact === null) return null;

  const blocked = impact.blocked;
  const needsReason = impact.reasonRequired;
  const trimmed = reason.trim();
  const confirmDisabled = needsReason && trimmed.length === 0;
  const policyKey = blocked ? 'block' : needsReason ? 'confirmWithReason' : 'warn';

  return (
    <Modal opened={opened} onClose={onClose} title={t(`sourceImpact.title.${action}`)} returnFocus={false} size="lg">
      <Stack gap="sm">
        <Text size="sm">
          {t(`sourceImpact.intro.${action}`, { approved: impact.approvedCount, total: impact.dependentCount })}
        </Text>

        {impact.consumers.length > 0 ? (
          <Stack gap={4} component="section" aria-label={t('sourceImpact.list')}>
            <Text size="sm" fw={600}>
              {t('sourceImpact.list')}
            </Text>
            <List size="sm" spacing={4} data-testid="source-impact-consumers">
              {impact.consumers.map((c) => (
                <List.Item key={`${String(c.documentId)}-${String(c.periodKey)}-${c.bindingCode}`}>
                  <Group gap="xs" wrap="wrap">
                    <Text size="sm" span>
                      {c.projectCode} · {c.documentKey} · {formatPeriodKey(c.periodKey)}
                    </Text>
                    <Badge size="sm" variant="light" color={c.approved ? 'statusWarning' : 'statusInfo'}>
                      {t(c.approved ? 'sourceImpact.status.approved' : 'sourceImpact.status.pending')}
                    </Badge>
                  </Group>
                </List.Item>
              ))}
            </List>
          </Stack>
        ) : null}

        {impact.hiddenCount > 0 ? (
          <Text size="sm" c="dimmed" data-testid="source-impact-hidden">
            {t('sourceImpact.hidden', { count: impact.hiddenCount })}
          </Text>
        ) : null}

        {blocked ? (
          <Alert color="statusError" data-testid="source-impact-blocked">
            {t('sourceImpact.policy.block')}
          </Alert>
        ) : (
          <Text size="sm" data-testid={`source-impact-policy-${policyKey}`}>
            {t(`sourceImpact.policy.${policyKey}`)}
          </Text>
        )}

        {needsReason && !blocked ? (
          <Textarea
            label={t('sourceImpact.reason.label')}
            value={reason}
            onChange={(event) => setReason(event.currentTarget.value)}
            minRows={3}
            autosize
            required
            data-testid="source-impact-reason"
          />
        ) : null}

        <Group justify="flex-end" gap="xs">
          <Button variant="default" data-autofocus onClick={onClose} data-testid="source-impact-cancel">
            {t('common.cancel')}
          </Button>
          {blocked ? null : (
            <Button
              color="statusWarning"
              loading={isPending === true}
              disabled={confirmDisabled}
              onClick={() => {
                // ⛔ B145: закритий діалог не підтверджує (анімація закриття ще ~200 мс тримає кнопку в DOM).
                if (opened) onConfirm({ hash: impact.impactHash, reason: needsReason ? trimmed : null });
              }}
              data-testid="source-impact-confirm"
            >
              {t(`sourceImpact.confirm.${action}`)}
            </Button>
          )}
        </Group>
      </Stack>
    </Modal>
  );
}
