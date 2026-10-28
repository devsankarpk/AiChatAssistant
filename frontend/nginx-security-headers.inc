# Browser-hardening headers, included in the server block AND in every location that sets its own add_header
# (nginx does not inherit add_header into a location that defines one, so a header added only at server level would
# silently vanish from those responses).
#
# Content-Security-Policy, in plain words: scripts only from this site (this is the rule that matters: it stops injected
# script from running); styles from this site plus inline styles (Angular injects component styles at runtime) and Google
# Fonts; fonts from Google; images from this site and data: URLs; connections (REST and the SignalR WebSocket) only to this
# same host; the page may not be framed by anyone; no plugins; forms and <base> only to this site.
add_header Content-Security-Policy "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com data:; img-src 'self' data:; connect-src 'self' ws://$http_host wss://$http_host; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'" always;
add_header X-Content-Type-Options "nosniff" always;
add_header X-Frame-Options "DENY" always;
add_header Referrer-Policy "strict-origin-when-cross-origin" always;
add_header Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=(), usb=()" always;
add_header Cross-Origin-Opener-Policy "same-origin" always;
# HSTS only when the request reached the proxy over HTTPS (a TLS-terminating load balancer in front sets X-Forwarded-Proto).
# An empty value makes nginx leave the header out, so plain-HTTP local use is never pinned to HTTPS.
add_header Strict-Transport-Security $hsts_header always;
