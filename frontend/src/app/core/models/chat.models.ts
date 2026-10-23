// Mirrors backend-dotnet/src/AiChatAssistant.Api/Dtos/Chat/ChatDtos.cs.

export interface SessionResponse {
  id: string;
  title: string;
  createdAt: string;
}

export interface MessageResponse {
  id: number;
  role: 'user' | 'assistant';
  content: string;
  createdAt: string;
}

/** `.NET -> Angular` send-message response shape, per CLAUDE.md's cross-service contracts. */
export interface SendMessageResponse {
  userMessage: MessageResponse;
  assistantMessage: MessageResponse;
}

/**
 * The send-message response when the server runs with async replies (`Chat:AsyncReplies`): `202`, the question is
 * saved and the reply is being produced. It arrives later as the hub event `AssistantReplied` (or `AssistantFailed`).
 */
export interface SendMessageAccepted {
  userMessage: MessageResponse;
  replyStatus: 'pending';
  jobId: string;
}

/** What POST .../messages can answer: the reply itself (200), or a promise of one (202). */
export type SendMessageResult = SendMessageResponse | SendMessageAccepted;

export function isAccepted(result: SendMessageResult): result is SendMessageAccepted {
  return 'replyStatus' in result;
}

/** A reply that is not delivered yet (GET .../sessions/{id}/jobs): still being produced, or given up on. */
export interface ReplyJob {
  jobId: string;
  userMessageId: number;
  status: 'pending' | 'failed';
  error?: string | null;
}

/** Hub event: the reply is ready (also saved, so a missed event is repaired by refetching). */
export interface AssistantRepliedEvent {
  jobId: string;
  sessionId: string;
  userMessageId: number;
  assistantMessage: MessageResponse;
}

/** Hub event: every attempt failed. */
export interface AssistantFailedEvent {
  jobId: string;
  sessionId: string;
  userMessageId: number;
  error: { code: string; message: string };
}
