import { BarChart3, ImageIcon } from 'lucide-react'
import type { CSSProperties } from 'react'
import { DataChart } from '@/components/charts/data-chart'
import { Spinner } from '@/components/ui/spinner'
import { cn } from '@/lib/utils'
import { chartResult, type DeckTheme, type Slide } from './decks-api'

/** The built-in look's theme: the app's blue and ink. */
const builtIn = { dk1: '#1B1C1F', lt1: '#FFFFFF', dk2: '#1E3A5F', lt2: '#EEF1F5', accent1: '#2A78D6' }

const rtl = /^[^\p{L}]*[֐-ࣿיִ-ﻼ]/u

/** A line of text, right to left when it reads so. */
function Line({ text, className }: { text: string; className?: string }) {
  return (
    <span dir={rtl.test(text) ? 'rtl' : undefined} className={className}>
      {text}
    </span>
  )
}

function Points({ items, className }: { items: string[]; className?: string }) {
  return (
    <ul className={cn('grid content-start gap-[1.1cqw] text-[2.3cqw] leading-snug', className)}>
      {items.map((b, i) => (
        <li key={i} className="flex gap-[1cqw]">
          <span className="text-[var(--deck-accent)]" aria-hidden="true">
            •
          </span>
          <Line text={b} />
        </li>
      ))}
    </ul>
  )
}

/**
 * One slide, 16:9, drawn as the PowerPoint file lays it out, in the template's colours and fonts; its sizes follow its
 * width (container units), so a thumbnail and a full screen are the same slide. A thumbnail leaves charts out.
 */
export function SlideView({ slide, theme, thumbnail, making, className }: { slide: Slide; theme?: DeckTheme | null; thumbnail?: boolean; making?: boolean; className?: string }) {
  const c = { ...builtIn, ...theme?.colors }
  const style = {
    '--deck-ink': c.dk1,
    '--deck-paper': c.lt1,
    '--deck-title': c.dk2,
    '--deck-muted': c.dk2,
    '--deck-accent': c.accent1,
    fontFamily: `${theme?.fonts?.minor ? `"${theme.fonts.minor}", ` : ''}Calibri, Carlito, Inter Variable, ui-sans-serif, system-ui, sans-serif`,
  } as CSSProperties
  const heading = { fontFamily: theme?.fonts?.major ? `"${theme.fonts.major}", Calibri, Carlito, Inter Variable, sans-serif` : undefined }
  const title = slide.title ?? ''
  const picture = slide.picture
  const pictureBox = (box: string) =>
    picture?.fileId ? (
      <img src={`/api/chat/attachments/${picture.fileId}/content`} alt={picture.prompt || title} className={cn('object-contain', box)} />
    ) : picture ? (
      <div className={cn('flex flex-col items-center justify-center gap-[1cqw] rounded-[0.6cqw] bg-[var(--deck-muted)]/8 p-[2cqw] text-center text-[1.5cqw] text-[var(--deck-muted)]', box)}>
        {making ? <Spinner label="Making the picture" /> : <ImageIcon className="size-[4cqw]" aria-hidden="true" />}
        {!thumbnail && <span className="line-clamp-3 italic">{picture.prompt}</span>}
      </div>
    ) : null

  return (
    <div
      className={cn('relative aspect-video w-full overflow-hidden bg-[var(--deck-paper)] text-[var(--deck-ink)] [container-type:inline-size]', className)}
      style={style}
    >
      <div className="absolute inset-x-0 top-0 h-[0.6%] bg-[var(--deck-accent)]" aria-hidden="true" />
      {slide.layout === 'title' && (
        <div className="absolute inset-x-[6%] top-[30%] grid gap-[1.6cqw]">
          <h2 className="text-[5cqw] leading-[1.05] font-bold text-[var(--deck-title)]" style={heading}>
            <Line text={title} />
          </h2>
          <div className="h-[0.35cqw] w-[11%] bg-[var(--deck-accent)]" aria-hidden="true" />
          {slide.subtitle && <Line text={slide.subtitle} className="text-[2.5cqw] text-[var(--deck-muted)]" />}
        </div>
      )}
      {slide.layout === 'section' && (
        <div className="absolute inset-x-[6%] top-[34%] grid gap-[1.6cqw]">
          <h2 className="text-[4.6cqw] leading-tight font-bold text-[var(--deck-title)]" style={heading}>
            <Line text={title} />
          </h2>
          <div className="h-[0.35cqw] w-[11%] bg-[var(--deck-accent)]" aria-hidden="true" />
          {slide.subtitle && <Line text={slide.subtitle} className="text-[2.1cqw] text-[var(--deck-muted)]" />}
        </div>
      )}
      {slide.layout === 'quote' && (
        <figure className="absolute inset-x-[10%] inset-y-[18%] flex flex-col items-center justify-center gap-[2cqw] text-center">
          <blockquote className="text-[3.3cqw] leading-snug italic">
            <Line text={`“${slide.quote ?? title}”`} />
          </blockquote>
          {slide.by && <figcaption className="text-[1.9cqw] text-[var(--deck-muted)]">— {slide.by}</figcaption>}
        </figure>
      )}
      {!['title', 'section', 'quote'].includes(slide.layout) && (
        <>
          <h2 className="absolute inset-x-[6%] top-[5%] flex h-[16%] items-end text-[3.75cqw] leading-tight font-bold text-[var(--deck-title)]" style={heading}>
            <Line text={title} className="line-clamp-2" />
          </h2>
          <div className="absolute inset-x-[6%] top-[25%] bottom-[7%] flex gap-[3cqw]">
            {slide.layout === 'two' && (
              <>
                <Points items={slide.left ?? []} className="flex-1" />
                <Points items={slide.right ?? []} className="flex-1" />
              </>
            )}
            {slide.layout === 'bullets' && (
              <>
                <Points items={slide.bullets ?? []} className="flex-[56]" />
                {picture && pictureBox('flex-[41] min-w-0 h-full')}
              </>
            )}
            {slide.layout === 'chart' && (
              <>
                <div className="relative min-w-0 flex-[66]">
                  {slide.chart && slide.chart.categories.length > 0 && slide.chart.series.length > 0 ? (
                    thumbnail ? (
                      <BarChart3 className="absolute inset-0 m-auto size-[12cqw] text-[var(--deck-accent)]" aria-hidden="true" />
                    ) : (
                      <DataChart result={chartResult(slide.chart)} chart={{ type: slide.chart.type, x: 'Category', y: slide.chart.series.map((s) => s.name) }} label={title} height="100%" />
                    )
                  ) : (
                    <p className="absolute inset-0 m-auto h-fit text-center text-[1.8cqw] text-[var(--deck-muted)] italic">{slide.chart?.note ?? 'No numbers yet.'}</p>
                  )}
                </div>
                {(slide.bullets?.length ?? 0) > 0 && <Points items={slide.bullets!} className="flex-[30] self-center text-[1.9cqw]" />}
              </>
            )}
            {slide.layout === 'picture' && (
              <div className="flex min-w-0 flex-1 flex-col items-center gap-[1.5cqw]">
                {pictureBox('min-h-0 w-full flex-1')}
                {slide.subtitle && <Line text={slide.subtitle} className="text-[1.5cqw] text-[var(--deck-muted)]" />}
              </div>
            )}
          </div>
        </>
      )}
    </div>
  )
}
