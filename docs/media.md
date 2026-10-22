# Media studio

Pictures from a few words; a picture changed by saying how, all of it or only the
part painted over; variations of it; the picture four times as large; and a short
clip that starts from it. **Workspace → Media studio** (`/media`).

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

**Made from** opens the picture it came from. **Download** saves the file; **Delete**
takes it out of the studio with its file (an upload stays in your files).

## What it needs

- The **imagegen** module and a picture model (Admin → Models); FLUX.2-klein is the
  default. Enlarge needs its upscaler, `image/upscale/RealESRGAN_x4plus.safetensors`
  in the model library: it is downloaded with the picture model's files
  (Comfy-Org/Real-ESRGAN_repackaged, 67 MB), and the picture server reads it from
  then on.
- For **Move**, the **videogen** module, a video model and the **Video** permission.
- The **Pictures** permission opens the studio. Each picture counts toward the
  person's pictures a day (Safeguards) and their credit for pictures; a clip toward
  their credit for video. Their words go through the safeguards' rules first.

Pictures and clips are the person's files: they count toward their room (Storage),
and the storage page shows them as **In the media studio**. Their cost is booked in
the gateway's log as the chat's, at the model's price per picture (or per second of
video), so usage, credit and chargeback count them.
