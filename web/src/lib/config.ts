/**
 * Website settings. Read from environment variables at runtime (Coolify or .env.development),
 * not at build time, so the same Docker image runs everywhere. Server-side only.
 */
export interface SiteConfig {
  /** Public address of the website without a trailing "/", e.g. https://aos2.example.org */
  siteUrl: string;
  /** Where the website reaches the backend, e.g. http://backend:8080 */
  backendUrl: string;
  /** Shared key for /api/internal (Backend:InternalKey) */
  internalKey: string;
  /** Development login instead of Discord */
  devLogin: boolean;
}

export function getConfig(env: Record<string, string | undefined> = process.env): SiteConfig {
  const required = (name: string) => {
    const value = env[name]?.trim();
    if (!value) throw new Error(`Environment variable ${name} is missing (see docs/backend.md).`);
    return value.replace(/\/+$/, "");
  };

  return {
    siteUrl: required("SITE_URL"),
    backendUrl: required("BACKEND_INTERNAL_URL"),
    internalKey: required("BACKEND_INTERNAL_KEY"),
    devLogin: env.AOS2_DEV_LOGIN === "true",
  };
}
