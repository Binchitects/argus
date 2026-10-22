# The media studio's video, cut in the sandbox: each shot's clip (made longer, its last frame held, when its narration
# is longer), the narration over it, captions from the narration, music under it (quieter while someone speaks), as one
# H.264 MP4. In: story.json ({"shots": [{"clip", "speech", "text", "seconds"}], "music", "rtl"}) and the files it names.
# Out: story.mp4, and a line of JSON with the video's length and each shot's start.
import json, subprocess

W, H, FPS = 1280, 720, 24
story = json.load(open('story.json'))


def length(path):
    r = subprocess.run(['ffprobe', '-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', path], capture_output=True, text=True)
    try:
        return float(r.stdout.strip())
    except ValueError:
        return 0.0


def stamp(t):
    ms = int(round(t * 1000))
    return f'{ms // 3600000:02d}:{ms // 60000 % 60:02d}:{ms // 1000 % 60:02d},{ms % 1000:03d}'


shots = story['shots']
inputs, video, audio, starts, captions = [], [], [], [], []
at = 0.0
for i, s in enumerate(shots):
    clip = length(s['clip'])
    spoken = length(s['speech']) if s.get('speech') else 0.0
    # Long enough for its clip and what is said over it (with a breath after).
    d = max(clip, spoken + 0.5 if spoken else 0.0, float(s.get('seconds') or 0), 1.0)
    inputs += ['-threads', '1', '-i', s['clip']]
    v = len(inputs) // 4 - 1
    video.append(f'[{v}:v]scale={W}:{H}:force_original_aspect_ratio=decrease:flags=lanczos,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={FPS},'
                 f'tpad=stop_mode=clone:stop_duration={d:.3f},trim=duration={d:.3f},setpts=PTS-STARTPTS[v{i}]')
    if spoken:
        inputs += ['-threads', '1', '-i', s['speech']]
        a = len(inputs) // 4 - 1
        audio.append(f'[{a}:a]aresample=44100,aformat=channel_layouts=stereo,apad=whole_dur={d:.3f},atrim=duration={d:.3f}[a{i}]')
    else:
        audio.append(f'anullsrc=r=44100:cl=stereo,atrim=duration={d:.3f}[a{i}]')
    if s.get('text'):
        captions.append((at + 0.15, at + max(spoken, 1.0) + 0.25, s['text']))
    starts.append(round(at, 3))
    at += d
total = at

with open('captions.srt', 'w') as f:
    for n, (a, b, text) in enumerate(captions, 1):
        f.write(f'{n}\n{stamp(a)} --> {stamp(min(b, total))}\n{text}\n\n')

parts = video + audio
parts.append(''.join(f'[v{i}]' for i in range(len(shots))) + f'concat=n={len(shots)}:v=1:a=0[cut]')
parts.append(''.join(f'[a{i}]' for i in range(len(shots))) + f'concat=n={len(shots)}:v=0:a=1[said]')
font = 'Noto Sans Arabic' if story.get('rtl') else 'Noto Sans'
style = f"FontName={font},FontSize=20,PrimaryColour=&H00FFFFFF,OutlineColour=&H80000000,BorderStyle=3,Outline=1,Shadow=0,MarginV=36,Alignment=2"
parts.append(f"[cut]subtitles=captions.srt:force_style='{style}'[out]" if captions else '[cut]null[out]')
if story.get('music'):
    inputs += ['-threads', '1', '-i', story['music']]
    m = len(inputs) // 4 - 1
    parts.append('[said]asplit=2[say][key]')
    parts.append(f'[{m}:a]aresample=44100,aformat=channel_layouts=stereo,volume=0.35,apad=whole_dur={total:.3f},atrim=duration={total:.3f},'
                 f'afade=t=in:d=1,afade=t=out:st={max(0.0, total - 2):.3f}:d=2[bed]')
    parts.append('[bed][key]sidechaincompress=threshold=0.03:ratio=8:attack=20:release=400[ducked]')
    parts.append('[say][ducked]amix=inputs=2:normalize=0:duration=first[mix]')
else:
    parts.append('[said]anull[mix]')

# Few threads: the sandbox allows a run 64 processes and 3 GB of address space, and each input's decoder and the
# encoder would take a thread a core.
r = subprocess.run(['ffmpeg', '-v', 'error', '-y', '-filter_threads', '1', '-filter_complex_threads', '1', *inputs, '-filter_complex', ';'.join(parts),
                    '-map', '[out]', '-map', '[mix]', '-c:v', 'libx264', '-threads', '2', '-x264-params', 'threads=2:lookahead-threads=1', '-preset', 'veryfast',
                    '-crf', '23', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '160k', '-movflags', '+faststart',
                    '-t', f'{total:.3f}', 'story.mp4'], capture_output=True, text=True)
print(json.dumps({'seconds': round(total, 2), 'starts': starts, 'error': r.stderr.strip()[-600:] if r.returncode else None}))
