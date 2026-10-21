/** What a sketch becomes, and what the model is asked, in words it follows: one block the chat runs or draws. */
export interface SketchAsk {
  key: 'page' | 'react' | 'diagram' | 'scene' | 'table'
  label: string
  prompt: string
}

export const sketchAsks: SketchAsk[] = [
  {
    key: 'page',
    label: 'A page',
    prompt:
      'Build this sketch as a working web page: one complete ```html block, with its CSS and JavaScript in it, laid out as the drawing is. Put sensible content where the sketch has scribbles or placeholders, and make what looks like a button or a field work.',
  },
  {
    key: 'react',
    label: 'A React component',
    prompt:
      'Build this sketch as a React component: one ```jsx block with a default export, styled with Tailwind classes, laid out as the drawing is, with sensible content and working controls.',
  },
  {
    key: 'diagram',
    label: 'A diagram',
    prompt: 'Turn this sketch into a diagram: one ```mermaid block with the boxes, arrows and labels it shows, and nothing it does not.',
  },
  {
    key: 'scene',
    label: 'A 3D scene',
    prompt: 'Make this sketch a 3D scene with the 3D tool: the shapes it shows, where they are and how big, seen as the drawing sees them.',
  },
  {
    key: 'table',
    label: 'A table',
    prompt: 'Read this sketch as a table: a Markdown table of what it shows, with a header row.',
  },
]

/** The sketch's file name: when it was drawn. */
export const sketchName = (at: Date) => `sketch-${at.toTimeString().slice(0, 8).replaceAll(':', '-')}.png`

/** One stroke of the pen (or of the eraser): its points in the board's own units (0..Width, 0..Height). */
export interface Stroke {
  color: string
  width: number
  erase: boolean
  points: { x: number; y: number }[]
}

export const BoardWidth = 1200
export const BoardHeight = 750

/** Each chat's drawing, kept while the page is open: drawn again on, a sketch changes its answer. */
export const drawings = new Map<string, { strokes: Stroke[]; background: HTMLImageElement | null }>()
