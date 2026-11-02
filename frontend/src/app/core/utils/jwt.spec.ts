import { JwtClaims, decodeJwtPayload, isExpired, rolesFromClaims } from './jwt';

/** base64url, as a real JWT is encoded (no padding, - and _ instead of + and /). */
function b64url(value: string): string {
  const bytes = new TextEncoder().encode(value);
  let binary = '';
  bytes.forEach((b) => (binary += String.fromCharCode(b)));
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function token(claims: unknown): string {
  return `${b64url('{"alg":"HS256"}')}.${b64url(JSON.stringify(claims))}.sig`;
}

describe('jwt helpers', () => {
  describe('decodeJwtPayload', () => {
    it('reads the claims of a normal token', () => {
      const claims = decodeJwtPayload(token({ sub: '7', email: 'a@example.com', role: 'Admin', exp: 123 }));

      expect(claims?.sub).toBe('7');
      expect(claims?.email).toBe('a@example.com');
      expect(claims?.role).toBe('Admin');
      expect(claims?.exp).toBe(123);
    });

    it('handles base64url characters and missing padding', () => {
      // Characters chosen so the encoded form contains - and _ .
      const claims = decodeJwtPayload(token({ sub: '1', email: '??>>~~@example.com', exp: 1 }));

      expect(claims?.email).toBe('??>>~~@example.com');
    });

    it('decodes non-ASCII text (names and emails with accents or Tamil letters)', () => {
      const claims = decodeJwtPayload(token({ sub: '1', email: 'சங்கர்@example.com', exp: 1, name: 'Zoë' }));

      expect(claims?.email).toBe('சங்கர்@example.com');
      expect(claims?.['name']).toBe('Zoë');
    });

    it('returns null when the token does not have three parts', () => {
      expect(decodeJwtPayload('')).toBeNull();
      expect(decodeJwtPayload('abc')).toBeNull();
      expect(decodeJwtPayload('a.b')).toBeNull();
      expect(decodeJwtPayload('a.b.c.d')).toBeNull();
    });

    it('returns null when the payload is not valid base64', () => {
      expect(decodeJwtPayload('a.!!!not-base64!!!.c')).toBeNull();
    });

    it('returns null when the payload is base64 but not JSON', () => {
      expect(decodeJwtPayload(`a.${b64url('this is not json')}.c`)).toBeNull();
    });

    it('never throws on garbage', () => {
      for (const junk of ['.', '..', 'a..c', ' . . ', '\u0000.\u0000.\u0000']) {
        expect(() => decodeJwtPayload(junk)).not.toThrow();
      }
    });

    it('does not check the signature (that is the server\'s job)', () => {
      expect(decodeJwtPayload(`${b64url('{}')}.${b64url('{"sub":"1","email":"x","exp":1}')}.totally-wrong`)).not.toBeNull();
    });
  });

  describe('rolesFromClaims', () => {
    const base = { sub: '1', email: 'a@example.com', exp: 1 };

    it('wraps a single role in an array', () => {
      expect(rolesFromClaims({ ...base, role: 'User' })).toEqual(['User']);
    });

    it('keeps an array of roles', () => {
      expect(rolesFromClaims({ ...base, role: ['User', 'Admin'] })).toEqual(['User', 'Admin']);
    });

    it('is empty when there is no role claim or no claims', () => {
      expect(rolesFromClaims(base)).toEqual([]);
      expect(rolesFromClaims(null)).toEqual([]);
      expect(rolesFromClaims({ ...base, role: '' })).toEqual([]);
    });
  });

  describe('isExpired', () => {
    const claims = (exp: number): JwtClaims => ({ sub: '1', email: 'a', exp });

    it('is false for a token that expires in the future', () => {
      expect(isExpired(claims(Math.floor(Date.now() / 1000) + 60))).toBeFalse();
    });

    it('is true for a token that expired', () => {
      expect(isExpired(claims(Math.floor(Date.now() / 1000) - 60))).toBeTrue();
    });

    it('is true at the exact moment of expiry', () => {
      jasmine.clock().install();
      try {
        jasmine.clock().mockDate(new Date(1_000_000_000));
        expect(isExpired(claims(1_000_000))).toBeTrue();
        expect(isExpired(claims(1_000_001))).toBeFalse();
      } finally {
        jasmine.clock().uninstall();
      }
    });

    it('treats an unreadable token (no claims) as expired', () => {
      expect(isExpired(null)).toBeTrue();
    });
  });
});
