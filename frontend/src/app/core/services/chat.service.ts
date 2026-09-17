import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { MessageResponse, SendMessageResponse, SessionResponse } from '../models/chat.models';

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

  sendMessage(sessionId: string, content: string): Observable<SendMessageResponse> {
    return this.http.post<SendMessageResponse>(`${this.base}/sessions/${sessionId}/messages`, { content });
  }
}
