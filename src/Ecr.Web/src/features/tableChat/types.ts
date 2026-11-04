import type { components } from '@/api/schema';

/**
 * Table chat (TC): ONLY aliases of the generated schema (`D-137`). The shape of every field comes from the
 * server (`TableDiscussionController`); the calls in `api.ts` are checked against the schema by
 * `__tests__/api.contract.test.ts`.
 */
type Schemas = components['schemas'];

export type DiscussionSummary = Schemas['TableDiscussionSummaryDto'];
export type DiscussionSummaryItem = Schemas['TableDiscussionSummaryItemDto'];
export type DiscussionMessage = Schemas['TableDiscussionMessageDto'];
export type DiscussionPage = Schemas['TableDiscussionPageDto'];
export type DiscussionMention = Schemas['TableDiscussionMentionDto'];
export type MentionCandidate = Schemas['TableDiscussionMentionCandidateDto'];
export type MentionTask = Schemas['TableDiscussionMentionTaskDto'];
export type MentionTasks = Schemas['TableDiscussionMentionTasksDto'];
export type PostMessageRequest = Schemas['PostTableDiscussionMessageRequest'];
export type EditMessageRequest = Schemas['EditTableDiscussionMessageRequest'];
export type MarkReadRequest = Schemas['MarkTableDiscussionReadRequest'];
