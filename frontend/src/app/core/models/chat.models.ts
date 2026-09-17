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
