import { useState, type JSX } from 'react';
import { Button, Checkbox, Group, Modal, SegmentedControl, Stack, Text, TextInput } from '@mantine/core';
import { useMutation } from '@tanstack/react-query';
import { t } from '@/shared/i18n';
import { showApiError } from '@/shared/ui/notify';
import { createStream, updateStream, type GasChangeResult, type GasStreamDetail } from './api';

interface Props {
  /** `null` — закрито; `'new'` — новий потік; потік — правка. */
  readonly target: GasStreamDetail | 'new' | null;
  readonly onClose: () => void;
  readonly onSaved: (result: GasChangeResult) => void;
}

/**
 * Форма потоку: назва (унікальна разом із «суша/море», як у FLERT), номер, розташування, кислий, HMB, документ HMB, група,
 * «прихований у списках». Id не вводиться — сервер дає наступний вільний (на нього посилаються події PI).
 */
export function StreamFormModal({ target, onClose, onSaved }: Props): JSX.Element | null {
  if (target === null) return null;
  return <Form key={target === 'new' ? 'new' : String(target.id)} target={target} onClose={onClose} onSaved={onSaved} />;
}

function Form({ target, onClose, onSaved }: Props & { readonly target: GasStreamDetail | 'new' }): JSX.Element {
  const existing = target === 'new' ? undefined : target;
  const [name, setName] = useState(existing?.name ?? '');
  const [number, setNumber] = useState(existing?.number ?? '');
  const [onshore, setOnshore] = useState(existing?.isOnshore ?? true);
  const [sour, setSour] = useState(existing?.isSour ?? false);
  const [hmb, setHmb] = useState(existing?.isHmb ?? false);
  const [hmbDoc, setHmbDoc] = useState(existing?.hmbDoc ?? '');
  const [group, setGroup] = useState(existing?.group === null || existing?.group === undefined ? '' : String(existing.group));
  const [hidden, setHidden] = useState(existing?.hidden ?? false);
  const groupValue = group.trim().length === 0 ? null : Number(group);
  const groupInvalid = groupValue !== null && !Number.isInteger(groupValue);

  const save = useMutation({
    mutationFn: () => {
      const body = {
        name: name.trim(),
        number: number.trim().length === 0 ? null : number.trim(),
        isOnshore: onshore,
        isSour: sour,
        isHmb: hmb,
        hmbDoc: hmbDoc.trim().length === 0 ? null : hmbDoc.trim(),
        group: groupValue,
        hidden,
      };
      return existing === undefined ? createStream(body) : updateStream(existing.id, body);
    },
    onSuccess: onSaved,
    onError: showApiError,
  });

  const canSave = name.trim().length > 0 && !groupInvalid && !save.isPending;

  return (
    <Modal
      opened
      onClose={onClose}
      title={existing === undefined ? t('gasComposition.stream.newTitle') : t('gasComposition.stream.editTitle', { name: existing.name })}
      data-testid="gas-stream-form"
    >
      <Stack gap="sm">
        {existing === undefined ? (
          <Text size="xs" c="dimmed">
            {t('gasComposition.stream.idHint')}
          </Text>
        ) : (
          <Text size="sm">
            {t('gasComposition.detail.streamId')}: <b>{existing.streamId}</b>
          </Text>
        )}
        <TextInput
          label={t('gasComposition.stream.name')}
          value={name}
          maxLength={200}
          required
          data-autofocus
          onChange={(event) => setName(event.currentTarget.value)}
        />
        <TextInput
          label={t('gasComposition.stream.number')}
          value={number}
          maxLength={100}
          onChange={(event) => setNumber(event.currentTarget.value)}
        />
        <Stack gap="xs">
          <Text size="sm" fw={500} id="gas-stream-location">
            {t('gasComposition.location')}
          </Text>
          <SegmentedControl
            aria-labelledby="gas-stream-location"
            value={onshore ? 'onshore' : 'offshore'}
            onChange={(value) => setOnshore(value === 'onshore')}
            data={[
              { value: 'onshore', label: t('gasComposition.onshore') },
              { value: 'offshore', label: t('gasComposition.offshore') },
            ]}
          />
        </Stack>
        <Group gap="lg">
          <Checkbox label={t('gasComposition.sour')} checked={sour} onChange={(event) => setSour(event.currentTarget.checked)} />
          <Checkbox label={t('gasComposition.detail.hmb')} checked={hmb} onChange={(event) => setHmb(event.currentTarget.checked)} />
          <Checkbox label={t('gasComposition.detail.hidden')} checked={hidden} onChange={(event) => setHidden(event.currentTarget.checked)} />
        </Group>
        <TextInput
          label={t('gasComposition.detail.hmbDoc')}
          value={hmbDoc}
          maxLength={255}
          onChange={(event) => setHmbDoc(event.currentTarget.value)}
        />
        <TextInput
          label={t('gasComposition.detail.group')}
          inputMode="numeric"
          value={group}
          error={groupInvalid ? t('gasComposition.editor.invalidNumber') : undefined}
          onChange={(event) => setGroup(event.currentTarget.value)}
        />
        {existing !== undefined && (
          <Text size="xs" c="dimmed">
            {t('gasComposition.stream.calcFieldsHint')}
          </Text>
        )}
        <Group justify="flex-end" mt="sm">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button
            disabled={!canSave}
            loading={save.isPending}
            onClick={() => {
              if (!canSave) return;
              save.mutate();
            }}
          >
            {t('gasComposition.editor.save')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
