# TretioDLSS

A Windows app that takes a live console picture (HDMI capture card or a PS Remote Play window), upscales it and generates extra frames, and shows it on a monitor or TV with as little added delay as possible. "Tretio" is Swedish for thirty: 30 fps in, 60 or more out.

## Features

- Capture cards through Media Foundation, with the card's sound passed through in sync. Window capture for PS Remote Play.
- Upscaling: RTX Video, FSR 1, NVIDIA Image Scaling, bicubic and bilinear.
- Frame generation: NVIDIA Frame Generation, RIFE on any GPU, and AMD Frame Generation, x2 to x4.
- Cadence detection that finds the real frames in a 60 Hz signal (30 real in 60), so only new frames are processed.
- Low-delay output window with VRR or vsync pacing, HDR output, TV mode and a stats overlay.

Real DLSS cannot run on captured video, because it needs motion vectors and depth from the game engine. The app uses what works on plain video instead.

## Build

Requires the .NET 10 SDK and Node.js.

```
dotnet build TretioDLSS.sln -c Debug
dotnet test TretioDLSS.sln
```

The frontend lives in `Frontend/` (`npm run dev`, `npm run lint`, `npm run build`). `tools/pack.ps1 -Version <v>` builds the installer.

Built with C# on .NET 10, PhotinoX, React with daisyUI, and a Direct3D 11 pipeline with a native output window.
