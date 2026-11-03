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
  /** Single-use: exchanged at POST /auth/refresh for a new pair. Optional only so older fixtures keep compiling. */
  refreshToken?: string;
  refreshExpiresAt?: string;
  user: UserSummary;
}

export interface MeResponse {
  id: number;
  email: string;
  roles: string[];
}

export interface ForgotPasswordRequest {
  email: string;
}

export interface ResetPasswordRequest {
  token: string;
  newPassword: string;
}

/**
 * What registering answers while the server requires email confirmation: the same for a new and an existing address (so it cannot be used
 * to find out who is registered), and no token - the person confirms through the emailed link first, then logs in.
 */
export interface RegistrationPendingResponse {
  message: string;
  verificationRequired: true;
}

export function isRegistrationPending(response: AuthResponse | RegistrationPendingResponse): response is RegistrationPendingResponse {
  return 'verificationRequired' in response;
}

export interface VerifyEmailRequest {
  token: string;
}

export interface ResendVerificationRequest {
  email: string;
}

/** Generic success body for forgot/reset-password - no token, no user summary. */
export interface AuthMessageResponse {
  message: string;
}
