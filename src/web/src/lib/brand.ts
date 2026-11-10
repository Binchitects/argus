/**
 * The names people see, set at build time (the repository's branding.env, or PRODUCT_NAME and CODE_INTEL_NAME in the
 * build's environment): the product's and its code-intelligence service's. Text uses these and never spells a name out.
 * The product's name an admin gave at run time (Settings → Branding) comes from /api/info and wins where it is known.
 */
export const productName: string = import.meta.env.VITE_PRODUCT_NAME || 'Farzan Arena'
export const codeIntelName: string = import.meta.env.VITE_CODE_INTEL_NAME || 'Farzan Graph'

/** The names a text people read was written with (the docs): the defaults. */
export const defaultProductName = 'Farzan Arena'
export const defaultCodeIntelName = 'Farzan Graph'

/** A text written with the default names (a page of the manual), with the names built. */
export function named(text: string): string {
  if (productName === defaultProductName && codeIntelName === defaultCodeIntelName) return text
  return text.replaceAll(defaultProductName, productName).replaceAll(defaultCodeIntelName, codeIntelName)
}
