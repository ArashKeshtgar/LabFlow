import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { RestrictedGate } from './RestrictedGate'

describe('RestrictedGate', () => {
  it('asks for a reason of at least ten characters before it opens anything, and can be dismissed', async () => {
    const onOpen = vi.fn(async () => {})
    const onCancel = vi.fn()
    render(<RestrictedGate label="V" action="Open this requisition" onOpen={onOpen} onCancel={onCancel} />)
    expect(screen.getByRole('heading', { name: 'Very restricted' })).toBeInTheDocument()
    const open = screen.getByRole('button', { name: 'Open this requisition' })
    expect(open).toBeDisabled()

    await userEvent.type(screen.getByLabelText(/Why do you need to open it/), 'short')
    expect(open).toBeDisabled()
    await userEvent.type(screen.getByLabelText(/Why do you need to open it/), ' but now long enough ')
    await userEvent.click(open)
    expect(onOpen).toHaveBeenCalledWith('short but now long enough')   // trimmed

    await userEvent.click(screen.getByRole('button', { name: 'Not now' }))
    expect(onCancel).toHaveBeenCalled()
  })

  it('shows a refusal instead of opening', async () => {
    render(<RestrictedGate label="R" action="Open" onOpen={async () => { throw new Error('No permission.') }} onCancel={() => {}} />)
    expect(screen.getByRole('heading', { name: 'Restricted' })).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText(/Why do you need to open it/), 'Covering the bench today')
    await userEvent.click(screen.getByRole('button', { name: 'Open' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('No permission.')
  })
})
