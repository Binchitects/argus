import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { Providers } from '@/app/providers'
import { fakeApi } from '@/test/utils'
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from '@/components/ui/dropdown-menu'
import { ProjectSubmenu } from './project-picker'

/** The chat's More menu, with the project's submenu in it. */
function ProjectPicker({ project }: { project: { id: string; name: string } | null }) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger>More</DropdownMenuTrigger>
      <DropdownMenuContent>
        <ProjectSubmenu project={project} />
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

async function openProjects(name: string) {
  await userEvent.click(screen.getByRole('button', { name: 'More' }))
  await userEvent.click(await screen.findByRole('menuitem', { name }))
}

describe('the session’s project in the IDE', () => {
  it('lists the projects the person may add to, the repository’s marked, and chooses one or none', async () => {
    const calls = fakeApi(null, {
      'GET /api/project': () => ({ json: { projects: [{ id: 'p1', name: 'Payments' }, { id: 'p2', name: 'Company wiki' }], suggested: 'p2', problem: null } }),
      'POST /api/project': () => ({ json: { said: 'In Company wiki.' } }),
    })
    const { rerender } = render(
      <Providers>
        <ProjectPicker project={{ id: 'p1', name: 'Payments' }} />
      </Providers>,
    )
    await openProjects('Project: Payments')
    expect(await screen.findByRole('menuitemcheckbox', { name: 'Payments' })).toHaveAttribute('aria-checked', 'true')
    expect(screen.getByRole('menuitemcheckbox', { name: /Company wiki/ })).toHaveTextContent("its repository's")
    await userEvent.click(screen.getByRole('menuitemcheckbox', { name: /Company wiki/ }))
    await waitFor(() => expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ project: 'p2' }))
    expect(await screen.findByText('In Company wiki.')).toBeInTheDocument()

    rerender(
      <Providers>
        <ProjectPicker project={{ id: 'p2', name: 'Company wiki' }} />
      </Providers>,
    )
    await openProjects('Project: Company wiki')
    await userEvent.click(await screen.findByRole('menuitem', { name: 'In no project' }))
    await waitFor(() => expect(calls.filter((c) => c.method === 'POST').at(-1)?.body).toEqual({ project: 'none' }))
  })

  it('says why when Arena cannot list them', async () => {
    fakeApi(null, { 'GET /api/project': () => ({ json: { projects: null, suggested: null, problem: 'https://llm.test did not answer.' } }) })
    render(
      <Providers>
        <ProjectPicker project={null} />
      </Providers>,
    )
    await openProjects('Project: No project')
    expect(await screen.findByRole('menuitem', { name: 'https://llm.test did not answer.' })).toBeInTheDocument()
  })
})
