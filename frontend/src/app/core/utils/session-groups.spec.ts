import { SessionResponse } from '../models/chat.models';
import { groupSessionsByRecency } from './session-groups';

describe('groupSessionsByRecency', () => {
  // Fixed "now": Wed 14 Oct 2026, 15:00 local.
  const now = new Date(2026, 9, 14, 15, 0, 0);
  const session = (id: string, createdAt: Date): SessionResponse => ({
    id,
    title: id,
    createdAt: createdAt.toISOString(),
  });

  it('buckets chats into Today, Yesterday, Previous 7 days and Older', () => {
    const groups = groupSessionsByRecency(
      [
        session('this-morning', new Date(2026, 9, 14, 0, 5)),
        session('last-night', new Date(2026, 9, 13, 23, 55)),
        session('five-days-ago', new Date(2026, 9, 9, 12, 0)),
        session('a-month-ago', new Date(2026, 8, 10, 12, 0)),
      ],
      now,
    );

    expect(groups.map((g) => g.label)).toEqual(['Today', 'Yesterday', 'Previous 7 days', 'Older']);
    expect(groups.map((g) => g.sessions[0].id)).toEqual(['this-morning', 'last-night', 'five-days-ago', 'a-month-ago']);
  });

  it('drops empty groups and keeps the incoming order within a group', () => {
    const groups = groupSessionsByRecency(
      [session('b', new Date(2026, 9, 14, 12, 0)), session('a', new Date(2026, 9, 14, 9, 0))],
      now,
    );

    expect(groups.length).toBe(1);
    expect(groups[0].label).toBe('Today');
    expect(groups[0].sessions.map((s) => s.id)).toEqual(['b', 'a']);
  });

  it('returns no groups for no chats', () => {
    expect(groupSessionsByRecency([], now)).toEqual([]);
  });
});
