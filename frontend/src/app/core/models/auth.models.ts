// Mirrors backend-dotnet/src/AiChatAssistant.Api/Dtos/Auth/*.cs.
// ASP.NET Core's default System.Text.Json options camelCase every property,
// so these field names match the wire format directly - no mapping layer needed.

export interface RegisterRequest {
  name: string;
  email: string;
  password: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface UserSummary {
  id: number;
  name: string;
  email: string;
  roles: string[];
}

export interface AuthResponse {
  token: string;
  expiresAt: string;
  user: UserSummary;
}

export interface MeResponse {
  id: number;
  email: string;
  roles: string[];
}

/** The .NET-wide error shape: `{ error: { code, message } }` (see CLAUDE.md). */
export interface ApiErrorResponse {
  error: {
    code: string;
    message: string;
  };
}
