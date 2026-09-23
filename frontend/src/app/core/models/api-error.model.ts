/** The .NET-wide error shape every endpoint uses: `{ error: { code, message } }` (see CLAUDE.md). */
export interface ApiErrorResponse {
  error: {
    code: string;
    message: string;
  };
}
