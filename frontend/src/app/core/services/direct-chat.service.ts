import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  ConversationResponse,
  DirectMessageResponse,
  MarkReadResponse,
  UserSearchResult,
} from '../models/direct.models';

/** The REST side of direct chat. Live updates come from DirectHubService instead. */
@Injectable({ providedIn: 'root' })
export class DirectChatService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBaseUrl}/direct`;

  searchUsers(search: string): Observable<UserSearchResult[]> {
    return this.http.get<UserSearchResult[]>(`${this.base}/users`, { params: new HttpParams().set('search', search) });
  }

  listConversations(): Observable<ConversationResponse[]> {
    return this.http.get<ConversationResponse[]>(`${this.base}/conversations`);
  }

  startConversation(userId: number): Observable<ConversationResponse> {
    return this.http.post<ConversationResponse>(`${this.base}/conversations`, { userId });
  }

  getMessages(conversationId: string, before?: number, limit?: number): Observable<DirectMessageResponse[]> {
    let params = new HttpParams();
    if (before !== undefined) {
      params = params.set('before', before);
    }
    if (limit !== undefined) {
      params = params.set('limit', limit);
    }
    return this.http.get<DirectMessageResponse[]>(`${this.base}/conversations/${conversationId}/messages`, { params });
  }

  sendMessage(conversationId: string, content: string): Observable<DirectMessageResponse> {
    return this.http.post<DirectMessageResponse>(`${this.base}/conversations/${conversationId}/messages`, { content });
  }

  /** Delete one of your own messages for everyone (the server clears its text). */
  deleteMessage(conversationId: string, messageId: number): Observable<DirectMessageResponse> {
    return this.http.delete<DirectMessageResponse>(`${this.base}/conversations/${conversationId}/messages/${messageId}`);
  }

  markRead(conversationId: string): Observable<MarkReadResponse> {
    return this.http.post<MarkReadResponse>(`${this.base}/conversations/${conversationId}/read`, {});
  }
}
