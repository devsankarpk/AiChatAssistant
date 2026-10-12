import { SessionResponse } from '../models/chat.models';

export interface SessionGroup {
  label: string;
  sessions: SessionResponse[];
}

const DAY_MS = 86_400_000;

/** Buckets chats by how recently they were created, in the viewer's local time. Input order is
 * kept inside each bucket (the API already returns newest first); empty buckets are dropped. */
export function groupSessionsByRecency(sessions: SessionResponse[], now: Date = new Date()): SessionGroup[] {
  const startOfToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const buckets: SessionGroup[] = [
    { label: 'Today', sessions: [] },
    { label: 'Yesterday', sessions: [] },
    { label: 'Previous 7 days', sessions: [] },
    { label: 'Older', sessions: [] },
  ];

  for (const session of sessions) {
    const created = new Date(session.createdAt).getTime();
    const index =
      created >= startOfToday ? 0 : created >= startOfToday - DAY_MS ? 1 : created >= startOfToday - 7 * DAY_MS ? 2 : 3;
    buckets[index].sessions.push(session);
  }

  return buckets.filter((bucket) => bucket.sessions.length > 0);
}
