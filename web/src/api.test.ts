import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, askWhy, isRestricted } from './api'

afterEach(() => vi.unstubAllGlobals())

describe('restricted items', () => {
  it('are recognised by the 403 that says so, and by nothing else', () => {
    expect(isRestricted(new ApiError(403, 'x', {}, { code: 'restricted', label: 'V' }))).toBe(true)
    expect(isRestricted(new ApiError(403, 'x', {}, {}))).toBe(false)
    expect(isRestricted(new ApiError(404, 'x', {}, { code: 'restricted' }))).toBe(false)
    expect(isRestricted(new Error('x'))).toBe(false)
  })

  it('ask for a reason in the browser dialog, trimmed, and nothing when the user declines', () => {
    const prompt = vi.fn().mockReturnValueOnce('  Patient is at the desk  ').mockReturnValueOnce(null).mockReturnValueOnce('   ')
    vi.stubGlobal('prompt', prompt)
    expect(askWhy()).toBe('Patient is at the desk')
    expect(prompt.mock.calls[0]?.[0]).toMatch(/consent directive/)
    expect(askWhy()).toBeNull()
    expect(askWhy()).toBeNull()
  })

  it('are opened through the override calls: a reason, and the path of the item', async () => {
    const fetcher = vi.fn(async () => new Response(JSON.stringify({ open: true }), { status: 200 }))
    vi.stubGlobal('fetch', fetcher)
    const { api } = await import('./api')
    await api.overrideOrder(7, 'Patient is at the desk')
    await api.overrideRequisition(9, 'Covering the bench')
    const calls = fetcher.mock.calls as unknown as [string, RequestInit][]
    expect(calls[0]?.[0]).toBe('/api/reception/electronic-orders/7/override')
    expect(JSON.parse(String(calls[0]?.[1].body))).toEqual({ reason: 'Patient is at the desk' })
    expect(calls[1]?.[0]).toBe('/api/lab/requisitions/9/override')
    expect(calls[1]?.[1].method).toBe('POST')
  })
})
