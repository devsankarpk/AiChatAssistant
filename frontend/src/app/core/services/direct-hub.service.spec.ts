import { fakeAsync, TestBed, tick } from '@angular/core/testing';

import { DirectMessageResponse } from '../models/direct.models';
import { AuthService } from './auth.service';
import { DIRECT_HUB_FACTORY, DirectHubService, HubLike, hubUrlFromApiBase } from './direct-hub.service';

class FakeHub implements HubLike {
  handlers = new Map<string, (...args: any[]) => void>();
  reconnectedCallbacks: Array<() => void> = [];
  closeCallbacks: Array<() => void> = [];
  startCalls = 0;
  stopCalls = 0;
  failNextStarts = 0;

  on(event: string, handler: (...args: any[]) => void): void {
    this.handlers.set(event, handler);
  }
  onreconnected(callback: () => void): void {
    this.reconnectedCallbacks.push(callback);
  }
  onclose(callback: () => void): void {
    this.closeCallbacks.push(callback);
  }
  async start(): Promise<void> {
    this.startCalls++;
    if (this.failNextStarts > 0) {
      this.failNextStarts--;
      throw new Error('cannot connect');
    }
  }
  async stop(): Promise<void> {
    this.stopCalls++;
  }
  invoked: Array<{ method: string; args: unknown[] }> = [];
  failInvoke = false;
  async invoke(method: string, ...args: unknown[]): Promise<unknown> {
    this.invoked.push({ method, args });
    if (this.failInvoke) {
      throw new Error('hub rejected');
    }
    return undefined;
  }
  emit(event: string, payload: unknown): void {
    this.handlers.get(event)!(payload);
  }
}

describe('hubUrlFromApiBase', () => {
  it('turns the same-origin API base into the hub path', () => {
    expect(hubUrlFromApiBase('/api')).toBe('/hubs/direct');
    expect(hubUrlFromApiBase('/api/')).toBe('/hubs/direct');
  });

  it('keeps the host of an absolute API base (development)', () => {
    expect(hubUrlFromApiBase('http://localhost:5291/api')).toBe('http://localhost:5291/hubs/direct');
  });
});

describe('DirectHubService', () => {
  let service: DirectHubService;
  let hub: FakeHub;
  let factoryCalls: Array<{ url: string; token: string }>;
  let token: string | null;

  beforeEach(() => {
    hub = new FakeHub();
    factoryCalls = [];
    token = 'jwt-1';
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { getToken: () => token } },
        {
          provide: DIRECT_HUB_FACTORY,
          useValue: async (url: string, tokenFactory: () => string) => {
            factoryCalls.push({ url, token: tokenFactory() });
            return hub;
          },
        },
      ],
    });
    service = TestBed.inject(DirectHubService);
  });

  it('connects to /hubs/direct with the current token and reports connected', async () => {
    await service.start();

    expect(factoryCalls).toEqual([{ url: '/hubs/direct', token: 'jwt-1' }]);
    expect(hub.startCalls).toBe(1);
    expect(service.connected()).toBeTrue();
  });

  it('asks for the token again each time (so a refreshed token is used on reconnect)', async () => {
    let seen = '';
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { getToken: () => token } },
        { provide: DIRECT_HUB_FACTORY, useValue: async (_u: string, f: () => string) => { seen = f(); token = 'jwt-2'; seen += '|' + f(); return hub; } },
      ],
    });
    await TestBed.inject(DirectHubService).start();

    expect(seen).toBe('jwt-1|jwt-2');
  });

  it('does not open a second connection when started twice', async () => {
    await service.start();
    await service.start();

    expect(factoryCalls.length).toBe(1);
    expect(hub.startCalls).toBe(1);
  });

  it('forwards the three hub events to the matching streams', async () => {
    await service.start();
    const received: DirectMessageResponse[] = [];
    const statuses: unknown[] = [];
    const reads: unknown[] = [];
    service.messageReceived$.subscribe((m) => received.push(m));
    service.statusChanged$.subscribe((e) => statuses.push(e));
    service.messagesRead$.subscribe((e) => reads.push(e));

    hub.emit('MessageReceived', { id: 5 });
    hub.emit('MessageStatusChanged', { conversationId: 'c', messageIds: [5], status: 'read' });
    hub.emit('MessagesRead', { conversationId: 'c', upToMessageId: 5 });

    expect(received).toEqual([{ id: 5 } as DirectMessageResponse]);
    expect(statuses).toEqual([{ conversationId: 'c', messageIds: [5], status: 'read' }]);
    expect(reads).toEqual([{ conversationId: 'c', upToMessageId: 5 }]);
  });

  it('forwards the delete, typing and presence events too', async () => {
    await service.start();
    const deleted: unknown[] = [];
    const typing: unknown[] = [];
    const presence: unknown[] = [];
    service.messageDeleted$.subscribe((e) => deleted.push(e));
    service.peerTyping$.subscribe((e) => typing.push(e));
    service.peerPresence$.subscribe((e) => presence.push(e));

    hub.emit('MessageDeleted', { conversationId: 'c', messageId: 9 });
    hub.emit('PeerTyping', { conversationId: 'c', userId: 2 });
    hub.emit('PeerPresenceChanged', { userId: 2, online: false, lastSeenAt: '2026-10-21T10:00:00Z' });

    expect(deleted).toEqual([{ conversationId: 'c', messageId: 9 }]);
    expect(typing).toEqual([{ conversationId: 'c', userId: 2 }]);
    expect(presence).toEqual([{ userId: 2, online: false, lastSeenAt: '2026-10-21T10:00:00Z' }]);
  });

  it('tells the server we are typing by invoking Typing with the conversation id', async () => {
    await service.start();

    await service.sendTyping('conv-1');

    expect(hub.invoked).toEqual([{ method: 'Typing', args: ['conv-1'] }]);
  });

  it('sends no typing signal before it is connected or after it is stopped', async () => {
    await service.sendTyping('conv-1');
    expect(hub.invoked).toEqual([]);

    await service.start();
    await service.stop();
    await service.sendTyping('conv-1');
    expect(hub.invoked).toEqual([]);
  });

  it('swallows a failed typing signal (a missed "typing…" is harmless)', async () => {
    await service.start();
    hub.failInvoke = true;

    await expectAsync(service.sendTyping('conv-1')).toBeResolved();
  });

  it('signals a reconnect so the state can refetch what it missed, and tracks dropped connections', async () => {
    await service.start();
    let reconnects = 0;
    service.reconnected$.subscribe(() => reconnects++);

    hub.closeCallbacks.forEach((cb) => cb());
    expect(service.connected()).toBeFalse();
    hub.reconnectedCallbacks.forEach((cb) => cb());

    expect(reconnects).toBe(1);
    expect(service.connected()).toBeTrue();
  });

  it('stops the connection and reports disconnected', async () => {
    await service.start();

    await service.stop();

    expect(hub.stopCalls).toBe(1);
    expect(service.connected()).toBeFalse();
  });

  it('can be started again after being stopped', async () => {
    await service.start();
    await service.stop();
    await service.start();

    expect(factoryCalls.length).toBe(2);
    expect(service.connected()).toBeTrue();
  });

  it('retries with a growing delay when the first connection attempt fails', fakeAsync(() => {
    hub.failNextStarts = 2;

    void service.start();
    tick(0);
    expect(service.connected()).toBeFalse();
    expect(hub.startCalls).toBe(1);

    tick(1000); // first retry after 1 s, fails again
    expect(hub.startCalls).toBe(2);
    expect(service.connected()).toBeFalse();

    tick(1999); // the second delay is 2 s: not yet
    expect(hub.startCalls).toBe(2);
    tick(1); // now it succeeds
    expect(hub.startCalls).toBe(3);
    expect(service.connected()).toBeTrue();
  }));

  it('does not keep retrying after stop()', fakeAsync(() => {
    hub.failNextStarts = 5;
    void service.start();
    tick(0);

    void service.stop();
    tick(60_000);

    expect(hub.startCalls).toBe(1);
  }));

  it('closes a connection that finished connecting after sign-out', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    hub.start = async () => { hub.startCalls++; await gate; };

    const starting = service.start();
    await Promise.resolve();
    await service.stop(); // signed out while still connecting
    release();
    await starting;

    expect(hub.stopCalls).toBe(1);
    expect(service.connected()).toBeFalse();
  });
});
