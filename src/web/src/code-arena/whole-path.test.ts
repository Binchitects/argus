import { describe, expect, it } from 'vitest'
import { wholePath } from './whole-path'

describe("a file's whole path", () => {
  it("joins the folder's path in its own separators, and keeps one in an added folder as it is", () => {
    expect(wholePath('/home/me/shop', 'src/app.ts')).toBe('/home/me/shop/src/app.ts')
    expect(wholePath('/home/me/shop/', 'src/app.ts')).toBe('/home/me/shop/src/app.ts')
    expect(wholePath('C:\\Users\\me\\shop', 'src/app.ts')).toBe('C:\\Users\\me\\shop\\src\\app.ts')
    expect(wholePath('/home/me/shop', '/srv/lib/util.ts')).toBe('/srv/lib/util.ts')
    expect(wholePath('C:\\Users\\me\\shop', 'D:/lib/util.ts')).toBe('D:/lib/util.ts')
  })
})
