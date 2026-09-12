# Hardware checks

The three items in [CLAUDE.md](../CLAUDE.md) → *Known unverified* that need a
person and a webcam. Do them **before M2**. M2's hover highlight *is* the
spatial mapping, and every grab it makes goes through the posture gate, so a
fault here would feel like an M2 bug that isn't one.

About 45 minutes in total. Every command runs from `E:\Company\Github\Freethrow`
in PowerShell.

When a check passes, tick it in CLAUDE.md and paste what it printed.
"Verified" without the output is how these stayed open.

## 0. Set up this PC (once)

A .NET runtime alone cannot build. `dotnet --list-sdks` has to print a line.

```powershell
winget install Microsoft.DotNet.SDK.8
```

Open a **new** terminal so the SDK is on PATH, then:

```powershell
.\tools\install.ps1
dotnet test tests\Freethrow.Core.Tests\Freethrow.Core.Tests.csproj
dotnet run --project demos\Freethrow.Demo.Preview -- --probe
```

**Pass:** the install ends with `Ready.`, every test passes, and the probe
reports `0 dropped` at about 25 fps or more. If it says camera access is
denied: Settings → Privacy & security → Camera → allow desktop apps.

## 1. Calibrate end to end (closes two items)

One wizard run closes both *no spatial profile has ever been produced* and
*`MaxViewAxisAlignment` fell back to its default*. It calibrates the **primary**
monitor.

```powershell
dotnet run --project demos\Freethrow.Demo.Preview -- --calibrate-grab
```

Nine steps: open hand, fist, pointing at the camera, resting position, four
corners (markers appear on the monitor), and a maximum-reach sweep. Nothing
starts until you press **Start capturing**. The bar fills only on frames where
your hand is tracked, 45 frames per pose.

**Step 3, pointing at the camera, is the one that failed last time.** Watch
`view` in the live line under the video. It should read clearly higher than it
did for the open hand and the fist. If the bar stalls, the model has lost the
hand: tilt it slightly until the skeleton comes back, then hold. **Redo this
step** until the bar fills.

On the results screen:

| It says | Pass | If not |
|---|---|---|
| `no grab past view X.XX` | anything **except `0.55`** | `0.55` is the built-in default. Pointing did not separate from flat by 0.1, or tracked fewer than 15 frames. Start over and take more care over step 3. |
| a warning about the resting hand | no warning | "maps near the edge" or "outside the screen": redo the corners with a more relaxed reach |

Then press **Test the mapping**:

- Reach toward each corner. **The dot lands on that marker.**
- Hold your hand still and lean toward the camera, then back. **The dot stays
  put.** This is the depth invariance the whole mapping rests on.

Press **Save profile**, then confirm:

```powershell
dotnet run --project demos\Freethrow.Demo.Preview -- --monitors
Get-Content $env:LOCALAPPDATA\Freethrow\gesture-profile.json
```

**Pass:** the primary monitor shows `mapping: calibrated <today>`, and
`MaxViewAxisAlignment` in the JSON is not `0.55`.

## 2. Two real hands

Closes *the two-hand path has never been tested with two real hands*. Its
untested part is duplicate suppression: two tracks locking onto one hand.

**a. One hand only, 15 seconds.** Keep the other hand out of frame.

```powershell
dotnet run --project demos\Freethrow.Demo.Preview -- --track 15
```

**Pass:** `two hands : 0 frames`, or a handful at most. A steady count with one
hand up is the phantom second track, the exact failure this check exists for.

**b. Both hands, 30 seconds.** Raise both. Close the left and hold it closed,
then close the right as well. Open both. Now close the right first.

```powershell
dotnet run --project demos\Freethrow.Demo.Preview -- --track 30
```

**Pass**, reading the live line (`[id HOLD|point|idle open … near …]`). The
line names hands by id, not left or right (handedness is unreliable), so
first note which id is which hand:

- The left shows `HOLD`, and the right **never** shows `HOLD` while the left
  is still closed.
- Second round, the right shows `HOLD`, even though the left may have been
  detected first.
- In the summary, `detections : … M while tracking` stays small next to the
  frame count. There should be no "the detector ran often" note.

**c. Hover follows the nearer hand**, in the preview window:

```powershell
dotnet run --project demos\Freethrow.Demo.Preview
```

- Reach one hand toward the screen. **It turns blue** (hovering), and the other
  stays dim.
- Bring both hands to the same distance. **The blue does not flicker** between
  them.
- Close either hand. **It turns orange**, and the other cannot take over until
  it opens.

## 3. The posture gate, live

This runs on the value check 1 measured, so do it after saving the profile. It
is the M1.5 acceptance in [PLAN.md](PLAN.md) → *Verification*, and it has never
been run on a measured gate.

In the preview window (`dotnet run --project demos\Freethrow.Demo.Preview`):

| Do | Pass |
|---|---|
| Point an open hand at the camera | skeleton goes **grey** (blocked) and never orange |
| Close the hand side-on | **orange** within about 100 ms |
| Open it only partway | back to **blue** promptly, not lingering |
| Grab, then turn your wrist through its natural range | stays **orange** throughout |

When one fails, the status line under the video (`open …  view …`) says whether
openness or the view angle is at fault.

## Afterwards

- All passed → tick the three items in CLAUDE.md with the output. M2 is clear
  to start.
- Calibration felt wrong for a second person → note it. The shipped defaults
  came from one hand.
