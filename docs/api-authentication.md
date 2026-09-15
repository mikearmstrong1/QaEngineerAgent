# API authentication

API mode requires `Quality__Api__Key` by default and refuses to start when it is missing or invalid. Supply a cryptographically random secret (for example 32 random bytes encoded as hex) through environment configuration or your deployment secret manager. Accepted keys contain 32–256 visible ASCII characters, with no spaces. Keep keys out of source control.

Send `Authorization: Bearer <key>` on every job request, including polling. Missing, malformed, duplicate, or incorrect credentials receive HTTP 401 with `WWW-Authenticate: Bearer`. Query-string keys and cookies are not accepted. Authorization happens before request-body processing and job creation. Comparisons use fixed-time SHA-256 digest comparison.

The landing page, `/health`, and `/ready` are public. All other endpoints, including future endpoints, are protected unless explicitly marked anonymous. Worker and CLI modes access storage directly and do not require an HTTP key.

For a loopback-only demo, explicitly set `Quality__Api__AllowAnonymous=true`. A configured key always takes precedence over that setting. `.env.example` opts into demo access. Compose otherwise defaults to authenticated mode: set `QUALITY_API_KEY` and `QUALITY_API_ALLOW_ANONYMOUS=false` in your untracked `.env`. Native .NET commands use the `Quality__Api__...` names; Compose translates the uppercase names.

Playwright smoke clients accept `QUALITY_API_KEY`; Compose supplies it to its smoke service. Traces are disabled for this smoke suite because headers can contain the secret. The independent reviewed execution adapter retains its existing evidence behavior. The Compose verification script reads the API key from resolved Compose configuration without printing it.

Use HTTPS through your deployment's trusted TLS endpoint whenever traffic leaves the local machine. This is a single shared service key with full job access, not per-user authorization or tenant isolation. Rotate it by replacing the configured secret and restarting API instances and clients together; overlapping keys are not supported. Avoid request-header logging at the application proxy.

After a Release build, run `python3 scripts/verify-auth.py`. It uses temporary storage and a generated key to verify fail-closed startup, anonymous probes, invalid bearer rejection, protected reads/writes, key precedence over demo mode, and absence of job side effects for rejected requests. `scripts/verify-modes.py` and local smoke tests verify the explicit demo path.
