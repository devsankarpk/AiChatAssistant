import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, InjectionToken, inject, signal } from '@angular/core';
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
  private readonly window = inject(DOCUMENT).defaultView;

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
  /** True between "the page is being left" and "the page came back from the cache": no retries then. */
  private suspended = false;
  /** Whether we have connected before in this sign-in, so a later connect is a "reconnect" that must resync. */
  private everConnected = false;
  private attempt = 0;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private connecting = false;

  constructor() {
    // A page that is being left (navigation, reload, tab closed, or parked in the browser's back/forward
    // cache) must close its socket itself. Otherwise the server keeps counting the person as online until
    // its own keep-alive timeout expires, about 30 seconds, which we saw in a real test run.
    const onPageHide = () => this.suspend();
    const onPageShow = (event: Event) => {
      if ((event as PageTransitionEvent).persisted && this.wanted) {
        this.suspended = false;
        void this.start(); // restored from the back/forward cache: reconnect
      }
    };
    this.window?.addEventListener('pagehide', onPageHide);
    this.window?.addEventListener('pageshow', onPageShow);
    inject(DestroyRef).onDestroy(() => {
      this.window?.removeEventListener('pagehide', onPageHide);
      this.window?.removeEventListener('pageshow', onPageShow);
    });
  }

  /** Idempotent: calling it again while connected or connecting does nothing. */
  async start(): Promise<void> {
    this.wanted = true;
    this.suspended = false;
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
      hub.onclose(() => {
        // SignalR's own automatic reconnect gives up after about 40 seconds. A deploy or a network outage
        // can last longer, and without this the page would stay silently disconnected until reloaded.
        if (this.hub === hub) {
          this.hub = null;
        }
        this.connected.set(false);
        this.scheduleRetry(); // does nothing after sign-out (stop) or while the page is being left (suspend)
      });

      await hub.start();
      if (!this.wanted) {
        await hub.stop(); // signed out while we were still connecting
        return;
      }
      this.hub = hub;
      this.attempt = 0;
      this.connected.set(true);
      if (this.everConnected) {
        this.reconnected$.next(); // we were away: let the state refetch whatever it missed
      }
      this.everConnected = true;
    } catch {
      this.connected.set(false);
      this.scheduleRetry();
    } finally {
      this.connecting = false;
    }
  }

  /** Closes the socket now but stays "wanted", so a page restored from the browser cache reconnects. */
  private suspend(): void {
    this.suspended = true;
    if (this.retryTimer) {
      clearTimeout(this.retryTimer);
      this.retryTimer = null;
    }
    const hub = this.hub;
    this.hub = null;
    this.connected.set(false);
    void hub?.stop().catch(() => undefined);
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
    this.everConnected = false;
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
    if (!this.wanted || this.suspended || this.retryTimer) {
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
