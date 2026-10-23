import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { MessageResponse, ReplyJob, SendMessageResult, SessionResponse } from '../models/chat.models';

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly http = inject(HttpClient);
  private readonly base = `${environment.apiBaseUrl}/chat`;

  getSessions(): Observable<SessionResponse[]> {
    return this.http.get<SessionResponse[]>(`${this.base}/sessions`);
  }

  createSession(title?: string): Observable<SessionResponse> {
    return this.http.post<SessionResponse>(`${this.base}/sessions`, { title });
  }

  getMessages(sessionId: string): Observable<MessageResponse[]> {
    return this.http.get<MessageResponse[]>(`${this.base}/sessions/${sessionId}/messages`);
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
