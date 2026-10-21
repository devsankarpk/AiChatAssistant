// Mirrors backend-dotnet/src/AiChatAssistant.Api/Dtos/Direct/DirectDtos.cs and the SignalR
// payloads pushed by Services/SignalRDirectNotifier.cs.

export type DirectMessageStatus = 'sent' | 'delivered' | 'read';

export interface UserSearchResult {
  id: number;
  name: string;
}

export interface PeerResponse {
  id: number;
  name: string;
}

export interface DirectMessageResponse {
  id: number;
  conversationId: string;
  senderId: number;
  /** Empty once the message is deleted (the server clears the text). */
  content: string;
  createdAt: string;
  status: DirectMessageStatus;
  deleted: boolean;
}

export interface ConversationResponse {
  id: string;
  peer: PeerResponse;
  lastMessage: DirectMessageResponse | null;
  unreadCount: number;
  peerOnline: boolean;
  peerLastSeenAt: string | null;
}

export interface MarkReadResponse {
  conversationId: string;
  readCount: number;
  upToMessageId: number | null;
}

/** `MessageStatusChanged`: these messages of yours are now delivered or read. */
export interface StatusChangedEvent {
  conversationId: string;
  messageIds: number[];
  status: 'delivered' | 'read';
}

/** `MessagesRead`: you read this conversation in another tab, so clear its unread count here too. */
export interface MessagesReadEvent {
  conversationId: string;
  upToMessageId: number;
}

/** `MessageDeleted`: the text of this message is gone for everyone. */
export interface MessageDeletedEvent {
  conversationId: string;
  messageId: number;
}

/** `PeerTyping`: the other person is typing in this conversation. */
export interface PeerTypingEvent {
  conversationId: string;
  userId: number;
}

/** `PeerPresenceChanged`: someone you share a conversation with came online or went offline. */
export interface PeerPresenceEvent {
  userId: number;
  online: boolean;
  lastSeenAt: string | null;
}
