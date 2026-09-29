# PrimeServe - headless RFB (VNC) server over a synthetic framebuffer

PrimeServe is a small, dependency-free **C# RFB (VNC) server** that serves an
in-memory synthetic framebuffer over the VNC wire protocol, with no X11 and no
real display. It exists to exercise a VNC client end to end: version and security
handshake, VNC-Authentication (challenge/response verified with an in-house DES),
ServerInit, RAW framebuffer updates in the client's negotiated pixel format, and
PointerEvent handling (a touch is reflected into the framebuffer as a marker, so
the touch-out direction is observable).

It is also structured as a **reusable server library plus a separate test
harness** (see "The reusable library, and the harness" below), so the server core
can be merged into another program that needs to serve its own content over VNC.

## Test screens

The harness has four test screens, each answering a different question about the
client's display path, chosen with `--screen`:

- **`pattern`** (default): `R = x & 0xFF`, `G = y & 0xFF`, `B = 0x40`. Every
  pixel is a known function of its position, so a decoded frame can be checked
  pixel by pixel.
- **`gradient`** (animated; `--animate MS` sets the step, default 100 ms): a
  gradient that shifts one pixel per step - red slides along x, green along y at
  twice the rate, blue along the diagonal the other way - so every pixel changes
  on every step and the client's page flipping, buffering and any per-pixel
  processing are exercised continuously; a stuck or torn frame is obvious at a
  glance. `--animate MS` on its own selects this screen.
- **`bars`**: three full-height vertical bars, pure red, pure green and pure
  blue, in that order from the left. The display must read R, G, B; any other
  order means the channel layout or the pixel-format negotiation is wrong.
- **`geometry`**: a one-pixel white border on all four edges (a missing or
  shifted side is a stride or offset error), a grey crosshair through the centre,
  white ticks at the quarter points of every edge, a green square centred on the
  crosshair whose side is the shorter display dimension less a margin, and a red
  circle inscribed in it (an ellipse means the aspect is wrong). A tap on the
  centre or a tick lands the white marker on a known mark, which checks the touch
  mapping numerically.
- **`black`**: every pixel black, and it stays black under taps (the server logs
  them but paints no marker). It exists to feed a client's black-frame rule - for
  example a backlight that turns off once the frame has been all black for a
  timeout; to test the wake, restart PrimeServe on a lit screen and let the
  client reconnect. It is not in the cycle, and asking for it with `--cycle` is a
  usage error.

**`--cycle S`** shows the four picture screens in turn, `S` seconds each, in the
order above starting from `--screen` (default `pattern`), the gradient stepping
during its own turn. The last touch marker is stamped back on top of every
repaint so a touch stays visible across screens. Frames are only ever sent in
answer to a client's update request (RFB is pull), so the client's own request
cadence throttles the rate; the schedule's deadlines are wall-clock and are
ticked from the client's message loop, with no timer thread.

Headless: there is no local window. Pure C# 5.0, `csc.exe`, references only
`mscorlib` + `System` (sockets, RNG, threads).

---

## Status

- **Builds clean** with `csc.exe` (.NET Framework 4.x), 0 warnings.
- **End-to-end interop verified**: version 3.8 handshake, VNC-auth DES
  challenge/response, `SetPixelFormat` (32bpp), a full RAW frame decoded to exact
  pixels, a pointer round-trip (touch -> server marker -> reflected white pixel),
  and a heartbeat (a non-incremental 1x1 request) answered.
- **Structured as a reusable library + a separate harness** (see below).

---

## Layout

```
PrimeServe/
  README.md            this file
  LICENSE              GPL-2.0-or-later; applies to the whole tree
  GPL-2.0.txt          the full GPL v2 text
  build.bat            csc.exe build -> build/PrimeServe.exe
  src/
    PixelFormat.cs     namespace PixelFormat - RFB PIXEL_FORMAT + RGB->client
                       encode (clsPixelFormat)                          [LIBRARY]
    Des.cs             namespace Des - DES + VNC key bit-reversal
                       (clsDes)                                         [LIBRARY]
    FrameBuffer.cs     namespace FrameBuffer - the reusable core: raster, dirty
                       flag, RAW encode, and the write API (clsFrameBuffer) [LIBRARY]
    RfbServer.cs       namespace RfbServer - the RFB protocol server, driven by
                       registered content callbacks (clsRfbServer)      [LIBRARY]
    TestScreens.cs     namespace TestScreens - the test screens, their schedule
                       and the pointer marker (clsTestScreens, enmScreen) [HARNESS]
    Program.cs         namespace PrimeServe - CLI; wires TestScreens as the
                       server's content driver (clsProgram)             [HARNESS]
  build/               build output (git-ignored)
```

---

## The reusable library, and the harness

The four **library** units are `clsPixelFormat` (`namespace PixelFormat`),
`clsDes` (`Des`), `clsFrameBuffer` (`FrameBuffer`) and `clsRfbServer`
(`RfbServer`) - each a `public` type in its own file and namespace, so a file
drops into another program's compile list with a `using`. They depend only on
the BCL portable core (`System`, `System.IO`, `System.Net`,
`System.Net.Sockets`, `System.Threading`) plus one CSPRNG
(`System.Security.Cryptography.RNGCryptoServiceProvider`, for the per-connection
VNC-auth challenge); DES is our own (`clsDes`), not the framework's.

The server knows nothing about what it serves. A consumer:

1. builds a `clsFrameBuffer(width, height)` and paints frames into it -
   `lock (fb.FB_SyncRoot()) { write fb.FB_Buffer(); fb.FB_MarkDirty(); }`;
2. builds a `clsRfbServer(port, password, fb, verbose)` and, if it has content
   that changes on a schedule or wants pointer events, registers callbacks with
   `SRV_SetHooks(nextEventMs, tick, describe, pointer)` - all optional, the
   do-nothing defaults serving a static buffer, waiting for update requests, and
   ignoring pointers;
3. calls `SRV_Run(once)`.

`TestScreens.cs` and `Program.cs` are the **harness** (`internal`): one consumer
of exactly that API - `clsTestScreens` paints the test screens through the
framebuffer write API and is registered as the server's content driver. They are
not part of the library and are left behind when the library is extracted for
reuse elsewhere.

---

## Build and run

```bat
REM from Windows (or via bash: cmd.exe //c "...\build.bat")
build.bat

REM the default 480x800 static pattern
build\PrimeServe.exe --password <password> --width 480 --height 800 --verbose

REM a smaller display, animated at 10 steps a second
build\PrimeServe.exe --password <password> --width 320 --height 240 --animate 100 --verbose

REM the channel-order and geometry checks, one screen for good
build\PrimeServe.exe --password <password> --screen bars --verbose
build\PrimeServe.exe --password <password> --screen geometry --verbose

REM every picture screen in turn, ten seconds each, starting with the colour bars
build\PrimeServe.exe --password <password> --screen bars --cycle 10 --verbose

REM nothing lit, for a client's black-frame rule
build\PrimeServe.exe --password <password> --screen black --verbose
```

`<password>` is any VNC password you choose; with no `--password` the server
offers the RFB "None" security type instead. `--password-env NAME` reads the
password from an environment variable rather than the command line.

### Command line

```
PrimeServe.exe [options]
  --port N            listen port (default 5900)
  --width N           framebuffer width (default 480)
  --height N          framebuffer height (default 800)
  --password P        VNC password (default: none / No-auth)
  --password-env NAME read the VNC password from env var NAME
  --screen NAME       test screen: pattern | gradient | bars | geometry | black
                      (default pattern; --animate alone selects gradient;
                      black is not in the cycle)
  --animate MS        gradient step period in ms (default 100 with --screen gradient;
                      only the gradient animates)
  --cycle S           show every screen in turn, S seconds each, starting with --screen
  --once              serve a single client, then exit (used by the test)
  -v, --verbose       per-message diagnostic logging
  -h, --help / --version
```

A PointerEvent paints a small white marker at the pointer on every screen except
`black`, and the marker survives a screen change.

---

Headless test rig; nothing is displayed locally.
