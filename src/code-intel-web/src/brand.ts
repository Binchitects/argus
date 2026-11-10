/**
 * The code-intelligence service's name people see, set at build time (the repository's branding.env, or CODE_INTEL_NAME
 * in the build's environment): vite.config.ts gives it as VITE_CODE_INTEL_NAME. Text uses it, never a name spelled out.
 */
export const codeIntelName: string = import.meta.env.VITE_CODE_INTEL_NAME || "Farzan Graph";
