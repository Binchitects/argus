/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** The product's name people see, set at build time (branding.env): src/lib/brand.ts. */
  readonly VITE_PRODUCT_NAME?: string
  /** The code-intelligence service's name people see, set at build time (branding.env): src/lib/brand.ts. */
  readonly VITE_CODE_INTEL_NAME?: string
}
