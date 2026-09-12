// Client-side JWT decoding is for UI conditionals only (show/hide an "Admin" link,
// display the user's name) - it is NOT authorization. The signature is never checked
// here; the server re-validates every token on every request. See CLAUDE.md.

/** The subset of claims backend-dotnet's TokenService puts in every token (sub, email, role). */
export interface JwtClaims {
  sub: string;
  email: string;
  role?: string | string[];
  exp: number;
  [claim: string]: unknown;
}

/** Decodes a JWT's payload without verifying its signature. Returns null if it isn't parseable. */
export function decodeJwtPayload(token: string): JwtClaims | null {
  const parts = token.split('.');
  if (parts.length !== 3) {
    return null;
  }

  try {
    const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
    const json = decodeURIComponent(
      atob(padded)
        .split('')
        .map((c) => '%' + c.charCodeAt(0).toString(16).padStart(2, '0'))
        .join(''),
    );
    return JSON.parse(json) as JwtClaims;
  } catch {
    return null;
  }
}

/** Normalizes the token's `role` claim (a single string or an array) into a string array. */
export function rolesFromClaims(claims: JwtClaims | null): string[] {
  if (!claims?.['role']) {
    return [];
  }
  return Array.isArray(claims['role']) ? (claims['role'] as string[]) : [claims['role'] as string];
}

/** True once `exp` (seconds since epoch) is in the past. */
export function isExpired(claims: JwtClaims | null): boolean {
  if (!claims) {
    return true;
  }
  return claims.exp * 1000 <= Date.now();
}
