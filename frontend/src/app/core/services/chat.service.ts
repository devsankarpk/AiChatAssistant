import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { MessageResponse, ReplyJob, ReplyStreamEvent, SendMessageResult, SessionResponse } from '../models/chat.models';
import { AuthService } from './auth.service';

/** Whether the chat page shows replies as they are written. Off keeps the one-shot request (the tests that predate streaming use it). */
export const STREAM_REPLIES = new InjectionToken<boolean>('STREAM_REPLIES', { providedIn: 'root', factory: () => true });

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
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

  /**
   * Sends a question and streams the answer. `fetch` instead of `HttpClient`, which cannot hand over a body piece by piece.
   * Unsubscribing is "Stop": the connection is closed and the server keeps what was written so far.
   * A failure before the first word errors with the server's usual `{ error: { code, message } }` body and status.
   */
  streamMessage(sessionId: string, content: string): Observable<ReplyStreamEvent> {
    return this.stream(`${this.base}/sessions/${sessionId}/messages/stream`, { content });
  }

  /** Asks for a fresh answer to the last question, streamed like `streamMessage` (no `user` event). */
  regenerate(sessionId: string): Observable<ReplyStreamEvent> {
    return this.stream(`${this.base}/sessions/${sessionId}/regenerate`, null);
  }

  private stream(url: string, body: unknown): Observable<ReplyStreamEvent> {
    return new Observable<ReplyStreamEvent>((subscriber) => {
      const abort = new AbortController();
      const fail = (status: number, error: unknown) => subscriber.error(new HttpErrorResponse({ status, error, url }));

      (async () => {
        let response: Response;
        try {
          const token = await this.auth.freshToken();
          response = await fetch(url, {
            method: 'POST',
            headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', Accept: 'text/event-stream' },
            body: body === null ? undefined : JSON.stringify(body),
            signal: abort.signal,
          });
        } catch (err) {
          if (!abort.signal.aborted) {
            fail(0, { error: { code: 'network_error', message: 'Could not reach the server.' }, cause: String(err) });
          }
          return;
        }

        if (!response.ok || !response.body) {
          const parsed = await response.json().catch(() => null);
          fail(response.status, parsed);
          return;
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        try {
          for (;;) {
            const { done, value } = await reader.read();
            if (done) {
              break;
            }
            buffer += decoder.decode(value, { stream: true });
            let end: number;
            while ((end = buffer.indexOf('\n\n')) >= 0) {
              const block = buffer.slice(0, end);
              buffer = buffer.slice(end + 2);
              const event = parseServerEvent(block);
              if (event && 'failure' in event) {
                fail(503, event.failure);
                return;
              }
              if (event) {
                subscriber.next(event);
                if (event.type === 'done') {
                  subscriber.complete();
                  return;
                }
              }
            }
          }
          // The connection closed without a `done`: the answer is incomplete.
          fail(503, { error: { code: 'ai_service_unavailable', message: 'The reply was cut off. Try again.' } });
        } catch {
          if (!abort.signal.aborted) {
            fail(503, { error: { code: 'ai_service_unavailable', message: 'The connection dropped before the reply finished.' } });
          }
        }
      })();

      return () => abort.abort();
    });
  }
}

/** One SSE block (`event: x` + `data: {...}`) as a stream step; `{ failure }` for an `error` event; null for anything else. */
function parseServerEvent(block: string): ReplyStreamEvent | { failure: unknown } | null {
  let name = '';
  let data = '';
  for (const line of block.split('\n')) {
    if (line.startsWith('event: ')) {
      name = line.slice(7);
    } else if (line.startsWith('data: ')) {
      data += line.slice(6);
    }
  }
  if (!name || !data) {
    return null;
  }
  let parsed: any;
  try {
    parsed = JSON.parse(data);
  } catch {
    return null;
  }
  switch (name) {
    case 'user':
      return { type: 'user', message: parsed };
    case 'delta':
      return { type: 'delta', text: parsed.text ?? '' };
    case 'done':
      return { type: 'done', message: parsed.assistantMessage };
    case 'error':
      return { failure: parsed };
    default:
      return null;
  }
}
