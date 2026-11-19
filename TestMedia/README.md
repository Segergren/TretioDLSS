# Test media

Clips live here and stay out of git. Make the synthetic ones with ffmpeg:

```
ffmpeg -f lavfi -i "mandelbrot=size=1920x1080:rate=30:maxiter=256" -t 20 -c:v libx264 -preset slow -crf 14 -pix_fmt yuv420p TestMedia/mandelbrot-1080p30.mp4
ffmpeg -f lavfi -i "testsrc2=size=1920x1080:rate=30" -t 20 -c:v libx264 -preset slow -crf 14 -pix_fmt yuv420p TestMedia/testsrc2-1080p30.mp4
```

Real gameplay: drop any recording here and resample it to 1080p at 30 fps:

```
ffmpeg -i game.mp4 -vf "scale=1920:1080,fps=30" -c:v libx264 -crf 16 -pix_fmt yuv420p TestMedia/game-1080p30.mp4
```

Play a clip as a console stand-in (30 fps content in a 60 Hz container):

```
TretioDLSS.exe --headless --source file:TestMedia/mandelbrot-1080p30.mp4@60 --frames 600 --report r.json
```

`raw/` holds recordings made with `--record-raw`.

## Live picture for the mock backend

`npm run dev:mock` plays JPEG frames from `TestMedia/mock-frames/` as the live picture, so UI work looks like the real thing. Make them from any gameplay clip (here 20 seconds at the preview's 30 fps and size):

```
ffmpeg -ss 30 -t 20 -i "<clip>.mp4" -vf "fps=30,scale=854:480" -q:v 4 TestMedia/mock-frames/f%03d.jpg
```

Another folder works with `node mock/server.mjs --frames=<dir>`.
