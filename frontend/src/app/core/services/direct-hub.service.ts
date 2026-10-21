import { Injectable, InjectionToken, inject, signal } from '@angular/core';
import { Subject } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  DirectMessageResponse,
  MessageDeletedEvent,
  MessagesReadEvent,
  PeerPresenceEvent,
  PeerTypingEvent,
  StatusChangedEvent,
} from '../models/direct.models';
import { AuthService } from './auth.service';

/** The few SignalR connection methods we use, so tests can supply a fake without the real library. */
export interface HubLike {
  on(event: string, handler: (...args: any[]) => void): void;
  onreconnected(callback: () => void): void;
  onclose(callback: () => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke(method: string, ...args: unknown[]): Promise<unknown>;
}

export type HubFactory = (url: string, accessTokenFactory: () => string) => Promise<HubLike>;

/**
 * The real factory. The SignalR client is imported on demand, so it becomes its own chunk that is only
 * downloaded once someone is signed in, not part of the initial bundle (login page).
 */
const createRealHub: HubFactory = async (url, accessTokenFactory) => {
  const signalR = await import('@microsoft/signalr');
  return new signalR.HubConnectionBuilder()
    .withUrl(url, { accessTokenFactory })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();
};

export const DIRECT_HUB_FACTORY = new InjectionToken<HubFactory>('DIRECT_HUB_FACTORY', {
  providedIn: 'root',
  factory: () => createRealHub,
});

/** `/api` -> `/hubs/direct`, `http://localhost:5291/api` -> `http://localhost:5291/hubs/direct`. */
export function hubUrlFromApiBase(apiBaseUrl: string): string {
  return apiBaseUrl.replace(/\/api\/?$/, '') + '/hubs/direct';
}

const MAX_RETRY_DELAY_MS = 30_000;

/**
 * One app-wide SignalR connection to /hubs/direct, started when someone signs in and stopped when they
 * sign out (see AppComponent). The JWT is read through a function each time the client connects or
 * reconnects, so it always uses the current token. Components never talk to the hub: they read
 * DirectStateService, which subscribes to these streams.
 */
@Injectable({ providedIn: 'root' })
export class DirectHubService {
  private readonly auth = inject(AuthService);
  private readonly factory = inject(DIRECT_HUB_FACTORY);

  readonly messageReceived$ = new Subject<DirectMessageResponse>();
  readonly statusChanged$ = new Subject<StatusChangedEvent>();
  readonly messagesRead$ = new Subject<MessagesReadEvent>();
  readonly messageDeleted$ = new Subject<MessageDeletedEvent>();
  readonly peerTyping$ = new Subject<PeerTypingEvent>();
  readonly peerPresence$ = new Subject<PeerPresenceEvent>();
  /** Emits after the browser reconnected: events may have been missed, so listeners should refetch. */
  readonly reconnected$ = new Subject<void>();

  readonly connected = signal(false);

  private hub: HubLike | null = null;
  private wanted = false;
  private attempt = 0;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private connecting = false;

  /** Idempotent: calling it again while connected or connecting does nothing. */
  async start(): Promise<void> {
    this.wanted = true;
    if (this.hub || this.connecting) {
      return;
    }
    this.connecting = true;
    try {
      const hub = await this.factory(hubUrlFromApiBase(environment.apiBaseUrl), () => this.auth.getToken() ?? '');
      hub.on('MessageReceived', (message: DirectMessageResponse) => this.messageReceived$.next(message));
      hub.on('MessageStatusChanged', (event: StatusChangedEvent) => this.statusChanged$.next(event));
      hub.on('MessagesRead', (event: MessagesReadEvent) => this.messagesRead$.next(event));
      hub.on('MessageDeleted', (event: MessageDeletedEvent) => this.messageDeleted$.next(event));
      hub.on('PeerTyping', (event: PeerTypingEvent) => this.peerTyping$.next(event));
      hub.on('PeerPresenceChanged', (event: PeerPresenceEvent) => this.peerPresence$.next(event));
      hub.onreconnected(() => {
        this.connected.set(true);
        this.reconnected$.next();
      });
      hub.onclose(() => this.connected.set(false));

      await hub.start();
      if (!this.wanted) {
        await hub.stop(); // signed out while we were still connecting
        return;
      }
      this.hub = hub;
      this.attempt = 0;
      this.connected.set(true);
    } catch {
      this.connected.set(false);
      this.scheduleRetry();
    } finally {
      this.connecting = false;
    }
  }

  /** Tells the other person we are typing. Best effort: dropped silently when not connected or on any error. */
  async sendTyping(conversationId: string): Promise<void> {
    if (!this.hub || !this.connected()) {
      return;
    }
    try {
      await this.hub.invoke('Typing', conversationId);
    } catch {
      // A missed "typing…" is harmless.
    }
  }

  async stop(): Promise<void> {
    this.wanted = false;
    if (this.retryTimer) {
      clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
    const hub = this.hub;
    this.hub = null;
    this.connected.set(false);
    if (hub) {
      try {
        await hub.stop();
      } catch {
        // Already closed: nothing to do.
      }
    }
  }

  /** The first connection attempt can fail (server restarting, offline); automatic reconnect only covers drops after that. */
  private scheduleRetry(): void {
    if (!this.wanted || this.retryTimer) {
      return;
    }
    const delay = Math.min(MAX_RETRY_DELAY_MS, 1000 * 2 ** this.attempt);
    this.attempt++;
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null;
      void this.start();
    }, delay);
  }
}
