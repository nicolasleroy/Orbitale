# Orbitale

Orbitale is a Windows 11 app that stacks a night-sky frame sequence into a star-trail (or circumpolar) image. It removes plane and satellite trails automatically, offers Lighten, Darken and Average blending, a comet style and gap filling, and shows the result building up live.

![Orbitale stacking a 279-frame sequence live: the star trails build up while removed plane and satellite trails flash in amber, and each thumbnail shows how many trails were removed from that frame](docs/images/orbitale-live-run.webp)

## What it does

1. Export your frames as JPG or TIFF from Photoshop (or any other tool).
2. Add the frames to Orbitale.
3. Choose the settings.
4. Press Start and watch the stacked result build live.
5. Press Stop, or let the run finish.
6. Save the result as a JPG or a 16-bit TIFF. The colour profile of the first frame is carried over.

## Features

- **Trail removal.** For each frame, the sky of its neighbouring frames is rotated into place, with lens distortion corrected. Anything new and line-shaped (a plane or satellite) is repaired with the real sky from those neighbours. This sky alignment only serves to find and repair trails: the stack itself is not aligned (a fixed tripod is assumed), so the stars still draw their trails.
- **Blend modes.** Lighten, Darken or Average.
- **Comet style.** A strength setting, and the head of the comet can be at the start or at the end of the trails.
- **Gap filling.** A strength setting.
- **Frame list.** Frames can be removed from the list before a run.
- **Remembered settings and folders.** Your settings are kept between sessions. The frame pickers open in the folder you last added frames from, and the save dialog in the folder you last saved to.

## Building

Requirements: Windows 11 and the .NET 10 SDK.

```
dotnet build -c Release
```

To publish a self-contained win-x64 app (no .NET install needed on the target PC):

```
dotnet publish src/Orbitale.App -c Release -r win-x64 --self-contained
```

The result is in `src\Orbitale.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\publish\`. Run `Orbitale.exe` from there, or copy the whole folder anywhere.

### Installer

`build-installer.ps1` runs the tests, publishes the app and builds `dist\Orbitale-Setup-<version>.exe` with [Inno Setup 6](https://jrsoftware.org/isinfo.php) (install it once with `winget install --id JRSoftware.InnoSetup -e`):

```
powershell -ExecutionPolicy Bypass -File build-installer.ps1
```

Add `-SkipTests` to skip the tests. The version comes from `<Version>` in `src\Orbitale.App\Orbitale.App.csproj`. The setup installs for the current user without admin rights (or for all users, if chosen), adds a Start menu entry and an optional desktop icon, and comes with an uninstaller; settings in `%LOCALAPPDATA%\Orbitale` are kept on uninstall.

## Running

**The app:** start `Orbitale.exe` (from the publish folder), or `dotnet run --project src/Orbitale.App -c Release`.

**The CLI harness:** a console tool that runs the same engine on a folder of frames and reports speed and memory.

```
dotnet run --project tools/Orbitale.Cli -c Release -- <inputDir> <output.tif|.jpg> [options]
```

| Option | Meaning |
| --- | --- |
| `--mode lighten\|darken\|average` | Blend mode |
| `--comet 0..1` | Turn on the comet style with this strength |
| `--head-at-start` | Put the comet head at the start of the trails |
| `--gapfill 0..1` | Turn on gap filling with this strength |
| `--no-trails` | Turn trail removal off |
| `--sensitivity 0..1` | Trail removal sensitivity |
| `--limit N` | Only use the first N frames |
| `--segments out.csv` | Write the removed trail segments (full-resolution pixels) to a CSV file |

Besides the output file, it writes a `_preview.jpg` next to it. The summary also counts the frames where sky alignment failed and trail removal was skipped. Ctrl+C stops the run and saves what has been stacked so far.

## Tests

```
dotnet test
```

## Where things live

Settings and the log are in `%LOCALAPPDATA%\Orbitale\` (`settings.json` and `orbitale.log`).

## Measured performance

On the developer PC (i7-11700K, 32 GB), for 279 frames of 40 MP:

| Run | Time | Peak memory |
| --- | --- | --- |
| Lighten with trail removal | about 6 minutes | about 5.2 GB |
| Comet + gap filling | about 7.5 to 8 minutes | about 6.6 GB |

Stop reacts in about 0.5 to 0.9 s, occasionally up to about 1.3 s in the first frames.

## Known limits

- A plane that stays in nearly the same place for several frames isn't removed.
- A short, slow plane piece that flies almost parallel to the stars' motion (within about 20 degrees) isn't removed.
- In a frame where a plane crossed, a bright star lying right on the trail's line can lose that one frame's dot.
- Very slow airliners crossing a dense star field can occasionally leave a faint ghost.
- A full run takes several minutes on a fast PC.
- 16-bit TIFF saves of 40 MP images take 10 to 30 s.
- EXIF from TIFF inputs isn't carried over. Only JPG first frames carry camera, lens and date into the output.
- Trail removal is skipped on frames where the sky can't be aligned with any neighbour (clouds, too few stars, a long lens or a long interval between frames). A notice appears the first time, and the final status gives the number of frames skipped. Frames whose long side is 2000 px or less still use the unaligned detection.
- Trail removal is off with fewer than 3 frames.
