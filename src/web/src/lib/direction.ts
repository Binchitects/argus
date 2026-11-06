/** The scripts written right to left: Hebrew, Arabic (Persian, Urdu), Syriac, Thaana, N'Ko and the rest. */
const rtl = /[\p{Script=Hebrew}\p{Script=Arabic}\p{Script=Syriac}\p{Script=Thaana}\p{Script=Nko}\p{Script=Samaritan}\p{Script=Mandaic}\p{Script=Adlam}\p{Script=Hanifi_Rohingya}]/u

/**
 * The direction of a text by its first letter, as dir="auto" decides it: "rtl" for a Persian, Arabic or Hebrew one,
 * "ltr" for any other script; null for a text with no letter (numbers, signs, code punctuation).
 */
export function textDirection(text: string): 'rtl' | 'ltr' | null {
  const first = /\p{L}/u.exec(text)?.[0]
  return first ? (rtl.test(first) ? 'rtl' : 'ltr') : null
}
