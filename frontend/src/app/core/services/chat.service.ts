import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { MessageResponse, ReplyJob, SendMessageResult, SessionResponse } from '../models/chat.models';

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBaseUrl}/chat`;

  /**
   * One page of chats, newest first (the server default and cap apply when `limit` is left out).
   * `before` is the `createdAt` of the last chat already received; `search` keeps titles containing the text.
   */
  getSessions(options: { search?: string; before?: string; limit?: number } = {}): Observable<SessionResponse[]> {
    let params = new HttpParams();
    if (options.search) {
      params = params.set('search', options.search);
    }
    if (options.before) {
      params = params.set('before', options.before);
    }
    if (options.limit) {
      params = params.set('limit', options.limit);
    }
    return this.http.get<SessionResponse[]>(`${this.base}/sessions`, { params });
  }

  renameSession(sessionId: string, title: string): Observable<SessionResponse> {
    return this.http.patch<SessionResponse>(`${this.base}/sessions/${sessionId}`, { title });
  }

  /** A soft delete on the server: the chat disappears, its usage history stays. */
  deleteSession(sessionId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/sessions/${sessionId}`);
  }

  createSession(title?: string): Observable<SessionResponse> {
    return this.http.post<SessionResponse>(`${this.base}/sessions`, { title });
  }

  /** The newest page of messages, oldest first; pass `before` (a message id) for the page just above it. */
  getMessages(sessionId: string, options: { before?: number; limit?: number } = {}): Observable<MessageResponse[]> {
    let params = new HttpParams();
    if (options.before !== undefined) {
      params = params.set('before', options.before);
    }
    if (options.limit) {
      params = params.set('limit', options.limit);
    }
    return this.http.get<MessageResponse[]>(`${this.base}/sessions/${sessionId}/messages`, { params });
  }

  /** 200 with both messages, or 202 with a job id when the server produces the reply in the background. */
  sendMessage(sessionId: string, content: string): Observable<SendMessageResult> {
    return this.http.post<SendMessageResult>(`${this.base}/sessions/${sessionId}/messages`, { content });
  }

  /** Replies not delivered yet for this chat (pending or failed). Always empty when the server answers synchronously. */
  getOpenReplyJobs(sessionId: string): Observable<ReplyJob[]> {
    return this.http.get<ReplyJob[]>(`${this.base}/sessions/${sessionId}/jobs`);
  }

  /** Asks for another go at a failed reply (no duplicate question). */
  retryReply(jobId: string): Observable<{ jobId: string; replyStatus: 'pending' }> {
    return this.http.post<{ jobId: string; replyStatus: 'pending' }>(`${this.base}/jobs/${jobId}/retry`, {});
  }
}
