# Hardware checks

The three items in [CLAUDE.md](../CLAUDE.md) → *Known unverified* that need a
person and a webcam. Do them **before M2**. M2's hover highlight *is* the
spatial mapping, and every grab it makes goes through the posture gate, so a
fault here would feel like an M2 bug that isn't one.

About 45 minutes in total. Every command runs from the repository root in
PowerShell.

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

**First, find where the camera can actually see your hand.** Two minutes, and
it decides how you calibrate. Skipping it is how the bottom corners got hit
twice: the wizard has no way to know your reach leaves the view until you are
already stuck on a step that cannot finish.

```powershell
dotnet run --project demos\Freethrow.Demo.Preview
```

Sit as you would to use Freethrow and reach out comfortably. Move your hand
around and note where the skeleton holds and where it drops. That region is
the most your working area can be, and the corners have to fit inside it. A
camera in a laptop lid is aimed at your face, so it usually sees a band at
chest-to-head height and nothing toward the desk.

Then choose:

- **Four corners fit inside what you found**, even as a smaller, higher
  rectangle than you would naturally reach: calibrate **full screen**, which
  maps both axes.

  ```powershell
  dotnet run --project demos\Freethrow.Demo.Preview -- --calibrate-grab
  ```

- **A bottom corner cannot be reached without leaving the view**: calibrate
  **side to side**. Only left-to-right is mapped; height is not measured, and
  nothing downstream guesses at it.

  ```powershell
  dotnet run --project demos\Freethrow.Demo.Preview -- --calibrate-grab --sideways
  ```

The mode can also be changed in the window under **Working area**, up until the
first reach step. After a side-to-side calibration, the next run starts in side
to side by itself.

Nine steps, or seven side to side: open hand, fist, pointing at the camera,
idle position, then four corners or a left and a right point (markers appear
on the monitor), then a maximum-reach sweep. Nothing starts until you press
**Start capturing**. The bar fills only on frames where your hand is tracked,
45 frames per pose.

**Side to side, height does not matter on the reach steps.** Reach left and
right at whatever height stays in view. Aiming for the marker's height, halfway
down the screen, is what takes a hand out of a lid camera's view. If the line
under the video says your hand left at the top or bottom, just raise or lower
it.

**Your working area does not need to be centred in the camera's view**, but it
does need to be *inside* it. On the pose steps the bar stops while any part of
the hand touches the frame edge, and the line under the video names the edge.
Move toward the middle and it resumes.

**Step 4, idle position:** rest your hands wherever they normally sit. If the
camera cannot see them there, press **My hands are out of frame**; that is a
normal answer, not a failure. Either way, no fist is asked for.

**Step 3, pointing at the camera, is the one that failed last time.** Watch
`view` in the live line under the video. It should read clearly higher than it
did for the open hand and the fist. If the bar stalls, the model has lost the
hand: tilt it slightly until the skeleton comes back, then hold. **Redo this
step** until the bar fills.

On the results screen:

| It says | Pass | If not |
|---|---|---|
| `no grab past view X.XX` | anything **except `0.55`** | `0.55` is the built-in default. Pointing did not separate from flat by 0.1, or tracked fewer than 15 frames. Start over and take more care over step 3. |
| a line about where hands rest | either "out of the camera's view" or a position — both are fine | — |
| what was fitted: *Full two-axis mapping* or *Side-to-side mapping* | the mode you chose | You chose full screen but it says *fitted side to side only*: your corners were too flat for height to be steerable, and it gives the numbers. That is a real result, not a failure. Keep it, or start over with more vertical reach |
| a warning that a corner or the maximum reach was at the camera's edge | no such warning | redo that corner with a shorter reach, or aim the camera toward your working area and start over. A clipped corner is where the camera stopped seeing, not where you reached. Side to side, the top and bottom of the view are expected and are not warned about |

Then press **Test the mapping**:

- **Full screen:** reach toward each corner. **The dot lands on that marker.**
  Hold your hand still and lean toward the camera, then back. **The dot stays
  put.** This is the depth invariance the whole mapping rests on.
- **Side to side:** the pointer is a **band** the height of the screen, not a
  dot, because a dot would claim a height nothing measured. Reach left and
  right: **the band lands on each marker**. Lean toward the camera and back:
  **the band stays put.** Hold your hand still: **the band stays within about
  one window's width.** That is the first real measurement of the thresholds
  that decide when height is too cramped to use. If it wanders further, note
  how far.

Press **Save profile**, then confirm:

```powershell
dotnet run --project demos\Freethrow.Demo.Preview -- --monitors
Get-Content $env:LOCALAPPDATA\Freethrow\gesture-profile.json
```

**Pass:** the primary monitor shows `mapping: calibrated <today>`, a `kind`
line naming the mode you chose, and an `idle` line; `MaxViewAxisAlignment` in
the JSON is not `0.55`.

**Laptop lid camera — does the calibration survive the lid?** A mapping is
fitted to one camera position, and tilting the lid moves the camera, so expect
a full-screen profile to go stale whenever you adjust the screen. A side-to-side
one *should* mostly survive: tilting turns the camera about a horizontal axis,
which moves the image up and down but barely sideways. That is reasoning, not
yet a measurement. In **Test the mapping**, hold your hand still, note where the
pointer sits, tilt the lid 5–10°, and look again. Write down whether it moved
and roughly how far. It goes in CLAUDE.md either way.

**One measurement while you are here.** On step 3, note `view` while pointing
at the lens from the middle of the frame, then again from near a side edge
(not touching it). They should read about the same: the view angle is meant
to be relative to the line from the camera to the hand, not to the camera's
centre axis. If the edge reading is clearly lower, write both numbers down;
the posture gate would then be stricter off-centre than in the middle, and
that needs its own fix.

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

**Pass**, reading the live line (`[id HOLD|point|idle open … conf … sees …]`). The
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
- Calibrated side to side → a supported result, and M2 can be built on it. But
  judge how M2 *feels* on a camera that sees both axes: in one dimension you
  cannot tell a bad design from the missing axis.
- Calibration felt wrong for a second person → note it. The shipped defaults
  came from one hand.
