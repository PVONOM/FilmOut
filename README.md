# FilmOut

FilmOut shows a folder of numbered images on a dedicated monitor, one at a time, so they can be
photographed onto film directly from the screen. In setup mode you give each image a display
time and a non-destructive grade, and see the result live on the output monitor. In run mode the
screen stays black until a trigger arrives (a key, a foot pedal, or a serial command), shows the
image for exactly its display time, and goes black again.

Display time is the exposure. It is counted in whole monitor refreshes and every exposure is
checked against the display's own frame statistics and logged.

This is phase 1 of the design: the output engine, setup and run modes, grading, the station
corrections (flat field and pixel defects), and the triggers. The calibrated negative model comes
next; until then negative mode uses a simple print-stock model.

## Requirements

- Windows 10 or 11, with the output monitor connected as its own display (not mirrored).
- The .NET 10 SDK (Visual Studio 2026 includes it; the command line works with the SDK alone).
- A Direct3D 11 graphics card. For an 8K output, at least 4 GB of graphics memory.

## Build and run

```
dotnet build FilmOut.slnx -c Release
dotnet run --project src/FilmOut.App -c Release
```

**This code has not been compiled yet.** It was written without access to a Windows toolchain. The
Direct3D calls in `src/FilmOut.Rendering/Renderer.cs` and `OutputEngine.cs` are written against
Vortice 3.8.2. If the first build reports a name or parameter type that doesn't match (for example
an `int` where Vortice wants a `uint`, or a property where it has a method), it will be there and
the fix is mechanical. The timing-critical DXGI calls go straight to the COM interface
(`DxgiTiming` in `Native.cs`) and don't depend on Vortice's naming.

## Before the first shoot

On the output monitor:

- Use one fixed picture mode and brightness. Turn off dynamic contrast, local dimming, eco modes,
  the ambient light sensor, sharpening and any "enhancement".
- Check the backlight doesn't flicker at your brightness setting (photograph the screen at a fast
  shutter speed and look for banding).

In Windows:

- Settings › Display: HDR off, Night light off, Auto colour management off for this display.
- Notifications: turn on Do not disturb while shooting.
- Graphics driver: set the output display to 10 bits per channel if the monitor supports it, and
  leave the driver's own dithering on.
- Pause Windows Update for the session.

In FilmOut, on the **Station** tab:

1. Choose the panel type. The notes below it are specific to it (for IPS: glow, viewing angle,
   flicker, and 8K connection notes).
2. Start the output on the monitor the camera faces. The refresh rate is measured from the
   display within a few seconds and shown top right.
3. Set the **camera gate**: show the framing chart, then adjust X, Y, width and height until the
   chart's border matches the camera's frame in the viewfinder. Images are fitted into the gate.
4. Optionally build a **flat-field map** and mark **pixel defects** (below).

## Working with a job

A job is a folder of images. Open it with **Open job folder**. Images are ordered by number
(`shot_2` before `shot_10`), and FilmOut warns about gaps or duplicated numbers. Settings are saved
automatically to `filmout.job.json` inside the folder; the images themselves are never changed.

The strip across the top shows the **shortest and longest exposure** in the job (with which image
and how many refreshes), the range in stops and printer points, and the total exposure time. Use
it to set up the camera before running.

**Image tab.** The display time in seconds, with buttons to step it by a stop or a printer point
(about 6%). Steps apply to every selected image. Below the time, FilmOut shows how many refreshes
it rounds to, what that comes to, and how far that is from the time entered. The image grade is
applied on top of the folder grade.

**Folder tab.** Positive or negative output, the encoding of the source files, rotation and flips,
how images fit the gate, the default display time, and the folder grade.

**Grades.** Each grade has three wheels with matching sliders, all applied in ACEScct log space:

- **Offset**: colour balance in stops. Its master slider is brightness. In log space an offset is
  exactly a change of exposure, so it behaves like a printer light.
- **Power**: midtones.
- **Slope**: highlights and contrast in log.

Contrast (about 18% grey) and saturation follow. Drag a wheel to shift colour, hold Shift for fine
control, and double-click a wheel or slider to reset it.

**Negative mode** (for print stock). Grade in positive terms: the grade describes the print you
want. FilmOut works out the light the print stock needs from the print contrast and density range
you enter, and you balance colour with printer lights (in points) as a lab would. A fitted
calibration profile will replace this model.

## Flat field

The flat-field map evens out light falloff from the lens and unevenness in the screen, including
soft dust shadows. To build one:

1. On the Station tab, choose **Show flat-field target**. The gate fills with an even field and
   five small black squares (one at each corner, plus one beside the top-left).
2. Photograph it through the taking lens at the aperture you'll shoot at, or expose a frame of the
   film you'll use, process and scan it. Keep the field well above clear film base everywhere,
   corners included.
3. Choose **Build map from a capture**, pick the file, and say how it was captured. Linear raw
   conversions or linear scans give the most accurate map.

FilmOut finds the markers (the capture can be rotated or mirrored) and builds a per-channel gain
map. Brighter areas are dimmed to match the dimmer ones, because a screen can't be made brighter
than full white, so the correction costs some light. The summary says how many stops; lengthen your
times by that much. Rebuild the map whenever the gate, lens, aperture, focus or monitor settings
change. Check a map with **With the current correction, to check it**.

## Pixel defects

Choose **Hunt for defects**, pick a field colour, and look at the output screen with a loupe. Move
the ring with the arrow keys (Shift for 10 pixels, Ctrl for 100) until it's centred on the bad
pixel, choose dead, stuck or weak and which channels, and press **M** or **Mark defect at cursor**.

Film can't resolve single pixels on an 8K screen, so the shader has the eight neighbours of a
defective pixel make up the light it can't give, or hold back the excess of a stuck one. A weak
pixel is first driven harder itself. Compensation is limited where the neighbours are already at
full brightness (for missing light) or black (for excess light).

## Run mode

On the Run tab, choose the image to start at and **Enter run mode**. The setup controls lock, the
output goes black, and the first image is loaded and **armed**. Each trigger then shows the armed
image for its time and goes back to black, and FilmOut prepares the next image. A trigger that
arrives before the next image is armed is ignored and logged, so a double press can't double-expose
a frame. **Abort exposure**, the abort key or `BLACK` cuts an exposure short and re-arms the same
image.

Every exposure goes into `filmout-run-<date>-<time>.csv` in the job folder: requested and measured
refreshes and seconds, the refresh rate, whether the display confirmed it, and the trigger source.
If an exposure's measured length differs from the requested one, the log says so.

To check the timing independently, tape a photodiode to the screen, read it with a
microcontroller, and compare the measured on-times with the log.

### Keyboard triggers

Set the trigger keys on the Run tab (for example `Space, Enter, F13`). They work in run mode
whichever window has focus. USB foot pedals and button boxes that act as keyboards work too.

### Serial protocol

Lines of ASCII text ending in a newline, at the baud rate you choose.

| From the controller | Meaning |
| --- | --- |
| `NEXT` | Show the armed image for its time |
| `GOTO n` | Go to image n (its position in the job) and arm it |
| `BLACK` | Cut the current exposure short; the same image is armed again |
| `STATUS?` | Ask for the current state |
| `PING` | Check the connection |

| From FilmOut | Meaning |
| --- | --- |
| `ARMED n` | Image n is loaded; the screen is black and waiting |
| `ON n` | The first refresh of image n is on the screen |
| `OFF n seconds frames` | The screen is black again; the measured exposure. `UNVERIFIED` is added if the display couldn't confirm it |
| `DONE` | Every image has been shown |
| `STATUS phase n` | Reply to `STATUS?` |
| `PONG` | Reply to `PING` |
| `ERR message` | A command was refused, with the reason |

A typical controller opens the shutter, sends `NEXT`, waits for `OFF`, closes the shutter, advances
the film, waits for `ARMED`, and repeats. Keeping the shutter closed between exposures matters most
on LCDs, whose black still glows. `tools/arduino/FilmOutController` is an example to adapt.

Note that FilmOut asserts DTR when it connects, which resets most Arduino boards.

## Files

| File | Where |
| --- | --- |
| Job settings | `filmout.job.json` in the job folder |
| Run logs | `filmout-run-*.csv` in the job folder |
| Station settings (monitor, gate, defects, triggers, serial) | `%AppData%\FilmOut\station.json` |
| Flat-field maps | `%AppData%\FilmOut\flatfield-*.ffmap` |

## How it works

- `FilmOut.Core`: the job and station models, exposure maths, statistics, the flat-field builder,
  the serial protocol and the run log. No Windows dependencies.
- `FilmOut.Rendering`: the output engine. A dedicated, high-priority thread owns a borderless window
  on the output display and a 10-bit flip-model swap chain with one frame of latency. Each image is
  rendered once by `Shaders/Pipeline.hlsl` into a prepared frame; on every refresh the engine either
  copies that frame to the screen or shows black. Exposures are counted in presents and measured with
  DXGI frame statistics.
- `FilmOut.App`: the WPF control window.

The display pipeline, per output pixel: map to the source image (rotation, flips, fit, offset),
decode to linear light, convert to ACEScct, apply the folder grade then the image grade, convert back
to linear, apply the negative model in negative mode, multiply by the flat-field gain, compensate
pixel defects, encode for the monitor's gamma, and add one step of dither before 10-bit output.

## Not yet done

- The calibration workflow: generated test charts, reading scanned charts, fitting the
  display-to-film-to-scanner model, the inverse 3D LUT, verification against reference images,
  and per-roll control strips.
- A soft-proof preview of the predicted print on the control monitor.
- Automatic defect detection from a high-resolution photo of the screen.
- 32-bit float and EXR/DPX sources (8- and 16-bit TIFF, PNG and JPEG are supported now).
