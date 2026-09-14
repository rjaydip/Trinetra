import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';

import { errorDetail } from '../../api/client';
import { api } from '../../api/endpoints';

/**
 * Free-form operator tags on one detection ("reviewed", "false positive", "priority-follow-up" —
 * anything an operator wants), distinct from and additive to `eventType`'s closed machine
 * classification. Every mutation invalidates every `detections` search (not just this one page's
 * query key) since the same detection can appear under a different filter combination and should
 * show its tags consistently everywhere.
 */
export function DetectionTags({ eventId, occurredAt, tags, canEdit }: {
  eventId: string; occurredAt: string; tags: string[]; canEdit: boolean;
}) {
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState('');
  const [error, setError] = useState<string | null>(null);

  const addTag = useMutation({
    mutationFn: (tag: string) => api.detections.addTag(eventId, { occurredAt, tag }),
    onSuccess: async () => {
      setDraft('');
      setError(null);
      await queryClient.invalidateQueries({ queryKey: ['detections'] });
    },
    onError: (reason) => setError(errorDetail(reason, 'Could not add the tag.')),
  });

  const removeTag = useMutation({
    mutationFn: (tag: string) => api.detections.removeTag(eventId, tag),
    onSuccess: async () => {
      setError(null);
      await queryClient.invalidateQueries({ queryKey: ['detections'] });
    },
    onError: (reason) => setError(errorDetail(reason, 'Could not remove the tag.')),
  });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const tag = draft.trim();
    if (tag) addTag.mutate(tag);
  }

  return <div className="detection-tags">
    <ul className="detection-tags__list" aria-label={`Tags for detection ${eventId}`}>
      {tags.length === 0 && <li className="detection-tags__empty">No tags</li>}
      {tags.map((tag) => <li key={tag} className="detection-tags__chip">
        {tag}
        {canEdit && <button type="button" aria-label={`Remove tag ${tag}`} disabled={removeTag.isPending} onClick={() => removeTag.mutate(tag)}>×</button>}
      </li>)}
    </ul>
    {canEdit && <form className="detection-tags__form" onSubmit={submit}>
      <label className="sr-only" htmlFor={`tag-input-${eventId}`}>Add a tag</label>
      <input id={`tag-input-${eventId}`} value={draft} maxLength={40} placeholder="Add tag…" onChange={(event) => setDraft(event.target.value)} />
      <button className="button button--secondary" type="submit" disabled={addTag.isPending || !draft.trim()}>Add</button>
    </form>}
    {error && <p className="form-error" role="alert">{error}</p>}
  </div>;
}
