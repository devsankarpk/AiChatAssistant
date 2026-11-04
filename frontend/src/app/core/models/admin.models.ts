// Mirrors backend-dotnet/src/AiChatAssistant.Api/Dtos/Admin/AdminDtos.cs.

export interface UsageLogResponse {
  id: number;
  userId: number;
  userName: string;
  userEmail: string;
  sessionId: string;
  sessionTitle: string;
  tokensUsed: number;
  promptTokens: number;
  completionTokens: number;
  /** Empty for rows written before cost tracking existed. */
  model: string;
  costEstimate: number;
  createdAt: string;
}

export interface PagedUsageLogResponse {
  items: UsageLogResponse[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface UsageTotals {
  requests: number;
  tokens: number;
  promptTokens: number;
  completionTokens: number;
  cost: number;
}

export interface UsageByUser {
  userId: number;
  userName: string;
  userEmail: string;
  requests: number;
  tokens: number;
  cost: number;
}

export interface UsageByDay {
  /** The UTC day, yyyy-MM-dd. */
  date: string;
  requests: number;
  tokens: number;
  cost: number;
}

/** Computed by the server over the whole date range, not over one page of rows. */
export interface UsageSummary {
  totals: UsageTotals;
  byUser: UsageByUser[];
  byDay: UsageByDay[];
}

export interface AdminUser {
  id: number;
  name: string;
  email: string;
  roles: string[];
  createdAt: string;
  lastSeenAt: string | null;
  disabledAt: string | null;
}

export interface PagedAdminUsers {
  items: AdminUser[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}
