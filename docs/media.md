# Media studio

Pictures from a few words; a picture changed by saying how, all of it or only the
part painted over; variations of it; the picture four times as large, or cut out of
its background; a short clip that starts from it; vector drawings (SVG) that move;
speech in a voice from a sample, and music. **Workspace → Media studio** (`/media`).

## Media studio

**Describe a picture**, choose its **Shape** (square, wide, photo, tall) and **How
many** (one, two or four), and press **Make it**. **Upload a picture** brings one of
yours in to work on.

Each piece of work runs in the background, in its turn on the GPU: the gallery
shows it where it will be, with what it is doing (waiting for the GPU, loading the
model, drawing). You may have three going at once. Work that takes more than a
minute says in the bell when it is done, so you can leave the page.

Open a picture to work on it:

- **Change**: say what changes ("give the fox a blue knitted hat"). It changes the
  whole picture as you say, keeping the rest of it. **Paint the part** to change
  only what you paint over: the picture model redraws it and the rest stays exactly
  as it was, the edge of the painted part blended softly. **Erase**, **Undo** and
  the brush's size help paint it.
- **Variations**: one, two or four pictures like it, **a little**, **some** or **a
  lot** different.
- **Enlarge**: four times as large, its details drawn in (Real-ESRGAN), up to 8,192
  pixels a side.
- **Move**: a clip of two to five seconds from the video model that starts from the
  picture; say what happens. It takes a few minutes.
- **Cut out**: the background taken away (BiRefNet), a PNG whose background is
  see-through, to put on slides, pages or other pictures.

**Made from** opens the picture it came from. **Download** saves the file; **Delete**
takes it out of the studio with its file (an upload stays in your files).

## Vector drawings

**Vector** makes an SVG: sharp at any size, small, and made of parts that can move.

- **Illustration** (with the picture model): the picture model draws it flat, and it
  is traced into clean vector shapes (vtracer). Its things are found and named as
  parts: the chat model names the things in your words (a fox, the moon, stars), a
  small model on the studio server finds where each is in the picture (CLIPSeg),
  and the shapes are grouped into them. Many of a kind (stars, birds) become a part
  whose pieces can move one after another. **Traced as** sets how closely it
  follows the picture: an illustration, more detail, an icon, a logo.
- **Drawn as SVG**: the model writes the SVG itself in a style (flat, line,
  isometric, gradient, icon, cute, geometric) and a shape. Best for icons, diagrams
  and simple shapes; what it got wrong (not XML, parts off the canvas, no names)
  goes back to it to fix, and a model that sees looks at its own drawing.
- **An SVG** of your own: it is made safe on the way in (no script, no outside
  references, nothing that is not drawn), and you are told what was taken out.
- A picture of yours: open it and choose **Vector** to trace it.

Open a drawing to work on it. Its stage plays its motions: pause, scrub the time,
or play it again and again.

- **Animate**: say what moves and how ("the stars twinkle one after another, the
  moon glows, the fox breathes"); the model plans the motions from its parts. Or
  **Pick a part** on the stage, **Give it a motion**, and set each motion by hand:
  what moves, how (fade, slide, pop, grow, spin, orbit, float, drift, bounce, sway,
  swing, wave, pulse, breathe, twinkle, glow, heartbeat, wiggle, shake, blink, draw
  the lines on, a colour change, or keyframes of your own), how long, when, once or
  in a loop. **Play these** keeps them. Motions are written into the SVG as CSS, so
  the file plays them anywhere: in a page, a slide, an `<img>`.
- **Change**: say what changes, or change **Its code** by hand: the stage shows it
  as you type, and **Use this code** keeps it.
- **Export**: a **PNG** (up to 4,096 pixels), a looping **GIF**, an **MP4** on a
  colour, a **WebM** that keeps its see-through background, or a **Sprite sheet**
  (every frame in one PNG, for games and pages). Moving ones are recorded by the
  stack's own Chromium (the **browser** module), frame by frame. Each goes into the
  gallery beside the drawing; **SVG** downloads the drawing itself.
- **Versions**: every change is kept (the last 50); **Put back** brings one back as
  a new version.

In a chat, the **Vector drawings** tool draws and animates SVG the same way
(`draw_svg`, `animate_svg`); each is shown in the chat as it plays and kept in the
studio, to change and export there.

## Videos

**Video** (with the picture and video models) makes a short narrated video:

1. Say what the video is about, and how many shots (3, 5 or 8). The model writes a
   storyboard: a title, and for each shot what is seen (in English, for the video
   model) and what the narrator says over it (in the language you wrote in).
2. Read and change it: each shot's words and length (2 to 5 seconds), shots added
   or taken out, the title, **the narrator** (your voice settings, Account → Voice,
   or a voice from a sample you may use), and **music under it** in words.
3. **Make the video**. Each shot is drawn by the picture model, then set moving from
   that picture by the video model (a few minutes a shot); the narration is spoken;
   the music is made for its length; then it is cut into one MP4 (1280 × 720): each
   shot as long as its clip or what is said over it, captions from the narration,
   the music quieter while the narrator speaks. The bell says when it is ready.

What was made is kept with the storyboard: a changed shot is made again alone (its
picture and clip when what is seen changes, its narration when what is said
changes), and **Make again** on a shot draws it anew. The video is in the gallery;
deleting the storyboard keeps it there.

## Speech and music

With the **studio** module, the card at the top also makes:

- **Speech**: words spoken (up to 3,000 characters), in the voice model's own voice
  or in **a voice from a sample**: record 10 to 30 seconds, or give a sound file.
  A sample's voice is used only when you tick that the voice is yours or that its
  owner lets you use it; what it says carries the model maker's inaudible
  watermark (Chatterbox's Perth), so it can be told apart as made by a model. **How
  lively** sets how much feeling it speaks with.
- **Music**: from a description (style, instruments, mood, tempo) and lyrics, or
  none for instrumental music; 15 seconds to 2 minutes (ACE-Step). Mark the lyrics'
  parts with `[verse]`, `[chorus]` and `[bridge]`.

Each comes as an MP3 in the gallery, under **Sound**.

## What it needs

- The **imagegen** module and a picture model (Admin → Models); FLUX.2-klein is the
  default. Enlarge needs its upscaler, `image/upscale/RealESRGAN_x4plus.safetensors`
  in the model library: it is downloaded with the picture model's files
  (Comfy-Org/Real-ESRGAN_repackaged, 67 MB), and the picture server reads it from
  then on.
- For **Move**, the **videogen** module, a video model and the **Video** permission.
- For vector drawings, the sandbox (tracing, PNGs and videos) and the **browser**
  module (recording moving ones). Their parts are found with the **studio** module's
  CLIPSeg (CIDAS/clipseg-rd64-refined, 603 MB, on its CPU), fetched with its other
  models; without it a traced drawing's parts are its islands of shapes.
- For **Cut out**, **Speech** and **Music**, the **studio** module
  (the `studio` feature): one service on the GPU with BiRefNet lite (178 MB),
  Chatterbox's English model (3.2 GB) and ACE-Step v1 3.5B (8.3 GB), fetched into
  the model library's `studio` folder with the default models (Admin → Models).
  One of them works at a time; it is loaded when asked for and unloaded after three
  minutes unused, so the GPU goes back to the chat models. One that does not fit in
  the GPU's free memory says so. Speech and music need the **Speech** permission;
  speech is priced as text read aloud, music per minute (Settings → Prices).
- The **Pictures** permission opens the studio. Each picture counts toward the
  person's pictures a day (Safeguards) and their credit for pictures; a clip toward
  their credit for video. Their words go through the safeguards' rules first.

Pictures and clips are the person's files: they count toward their room (Storage),
and the storage page shows them as **In the media studio**. Their cost is booked in
the gateway's log as the chat's, at the model's price per picture (or per second of
video), so usage, credit and chargeback count them.
