// Mirrors backend-dotnet/src/AiChatAssistant.Api/Dtos/Account/AccountDtos.cs.

export interface ProfileResponse {
  id: number;
  name: string;
  email: string;
  roles: string[];
  createdAt: string;
  /** False only for someone who registered while confirmation was required and has not confirmed (they cannot sign in until they do). */
  emailVerified: boolean;
}

export interface UpdateProfileRequest {
  name: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface DeleteAccountRequest {
  password: string;
}
