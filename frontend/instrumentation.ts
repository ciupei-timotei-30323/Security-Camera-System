/**
 * Next.js instrumentation hook: runs once when the server starts (both `next dev` and the
 * standalone production server). Security-relevant misconfiguration must stop the process here
 * rather than surface as a broken login for the first visitor.
 */
export async function register(): Promise<void> {
  const { assertSessionSecretConfigured } = await import("@/lib/session");

  if (process.env.NODE_ENV === "production") {
    // Throwing aborts server startup.
    assertSessionSecretConfigured();
  } else {
    try {
      assertSessionSecretConfigured();
    } catch (error) {
      console.warn(
        `[bff] ${error instanceof Error ? error.message : "BFF_SESSION_SECRET is not configured."} ` +
          "Login will fail until it is set.",
      );
    }
  }
}
