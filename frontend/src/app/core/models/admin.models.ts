// Mirrors backend-dotnet/src/AiChatAssistant.Api/Dtos/Admin/AdminDtos.cs.

export interface UsageLogResponse {
  id: number;
  userId: number;
  userName: string;
  userEmail: string;
  sessionId: string;
  sessionTitle: string;
  tokensUsed: number;
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
