/*------------------------------------------------------------------------------*/
/* SPDX-License-Identifier: GPL-2.0-or-later                                    */
/* Copyright (c) 2026 B. C. Services                                            */
/*                                                                              */
/* Licensed under the GNU General Public License version 2 or later --          */
/* see LICENSE beside this source.  GPL-2.0 full text in GPL-2.0.txt.           */
/*------------------------------------------------------------------------------*/
/* TestScreens.cs:                                                              */
/*                                                                              */
/* The PrimeServe test harness's screen generators and their schedule.  This    */
/* is NOT part of the reusable library: it paints deterministic test pictures   */
/* into a clsFrameBuffer through that buffer's public write API, exactly as a   */
/* real consumer would paint its own content, which is what proves the write    */
/* API is a genuine one.  The server drives this through the callbacks it       */
/* registers (next-event / tick / describe / pointer); the library server never */
/* names this file.                                                             */
/*                                                                              */
/* The pictures: a static pattern whose pixels are a known function of (x, y),  */
/* an animated gradient, colour bars, a geometry figure, and an all-black       */
/* screen, plus a pointer marker so a touch round-trip is observable.  All      */
/* mutation takes the framebuffer's own lock (clsFrameBuffer.FB_SyncRoot) so a  */
/* paint and the server's encode never overlap.                                 */
/*------------------------------------------------------------------------------*/

using System;
using FrameBuffer;

namespace TestScreens
{
    /*------------------------------------------------------------------------*/
    /* enmScreen:                                                             */
    /*                                                                        */
    /* The test screens clsTestScreens can paint.  One is chosen on the       */
    /* command line and painted once (TS_FillScreen); only ScreenGradient     */
    /* changes afterwards, one step per TS_AnimateStep.  Each screen answers  */
    /* a different question about the client's display path.  The first four  */
    /* form the cycle; ScreenBlack sits outside it.                           */
    /*------------------------------------------------------------------------*/

    internal enum enmScreen
    {
        ScreenPattern  = 0,   // static: red rises with x, green with y, blue constant - the pixels the loopback test asserts
        ScreenGradient = 1,   // animated: three ramps drifting in three directions - exercises flush, pan and flip every frame
        ScreenBars     = 2,   // static: three vertical bars, red then green then blue left to right - proves the channel order
        ScreenGeometry = 3,   // static: border, centred square with its inscribed circle, crosshair, quarter ticks - proves stride, aspect and touch mapping
        ScreenBlack    = 4    // static: every pixel black, taps not painted - feeds the client's black-frame backlight rule; not in the cycle
    }

    /*--------------------------------------------------------------------------*/
    /* clsTestScreens:                                                          */
    /*                                                                          */
    /* Paints the test screens into a clsFrameBuffer and owns the schedule that */
    /* shows them (TS_Start, TS_NextEventMs, TS_Tick: one screen for good, or   */
    /* all of them in turn, with the gradient stepping while it is up) plus the */
    /* last pointer marker.  It borrows the framebuffer's lock so its schedule  */
    /* state, its pixel writes and the server's encode are all mutually         */
    /* exclusive, as they were when the framebuffer carried this itself.        */
    /*--------------------------------------------------------------------------*/

    internal sealed class clsTestScreens
    {
        private const int TS_GEOMETRY_MARGIN = 10;   // pixels between the geometry square and the nearer panel edge
        private const int TS_GEOMETRY_TICK   = 8;    // length in pixels of the geometry screen's quarter-point ticks
        private const int TS_CYCLE_COUNT     = 4;    // enmScreen members the cycle walks (0..3); ScreenBlack is outside it

        private readonly clsFrameBuffer TS_fb;       // the framebuffer painted into
        private int             TS_iPhase;           // animation phase; advances once per TS_AnimateStep
        private bool            TS_bHaveMarker;      // true once a pointer marker has been drawn
        private int             TS_iMarkerX;         // last marker centre x, re-stamped after each repaint
        private int             TS_iMarkerY;         // last marker centre y
        private enmScreen       TS_screen;           // the screen currently painted
        private int             TS_iAnimateMs;       // gradient step period in ms; 0 = the gradient never steps
        private int             TS_iCycleMs;         // ms each screen stays up when cycling; 0 = one screen for good
        private int             TS_iNextStepTick;    // Environment.TickCount at which the gradient next steps
        private int             TS_iNextSwitchTick;  // Environment.TickCount at which the next screen comes up

        /*--------------------------------------------------------------------*/
        /* clsTestScreens (constructor):                                      */
        /*                                                                    */
        /* Binds to a framebuffer and sets the default schedule (the pattern, */
        /* no stepping, no cycling).  No screen is painted until TS_Start, so */
        /* the caller decides the first screen from the command line.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     fb : the framebuffer to paint into (already allocated).        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     clsTestScreens : a harness bound to fb, nothing painted yet.   */
        /*--------------------------------------------------------------------*/

        public clsTestScreens(clsFrameBuffer fb)
        {
            TS_fb = fb;

            /* Animation starts at phase 0 and no touch has been seen yet. */
            TS_iPhase      = 0;
            TS_bHaveMarker = false;
            TS_iMarkerX    = 0;
            TS_iMarkerY    = 0;

            /* No schedule: the pattern, no stepping, no cycling. */
            TS_screen          = enmScreen.ScreenPattern;
            TS_iAnimateMs      = 0;
            TS_iCycleMs        = 0;
            TS_iNextStepTick   = 0;
            TS_iNextSwitchTick = 0;
        }

        /*--------------------------------------------------------------------*/
        /* TS_Start:                                                          */
        /*                                                                    */
        /* Sets the screen schedule and paints its first screen.  With no     */
        /* cycle period the chosen screen stays up for good, the gradient     */
        /* stepping every iAnimateMs if it is the one chosen.  With a cycle   */
        /* period every screen comes up in enmScreen order, starting from the */
        /* chosen one, each for iCycleMs, the gradient stepping in its own    */
        /* turn.  The deadlines are wall-clock (Environment.TickCount), so a  */
        /* busy client cannot slow the schedule down; the server learns how   */
        /* long to wait from TS_NextEventMs and acts through TS_Tick.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     screen     : the first (or only) screen.                       */
        /*     iAnimateMs : gradient step period in ms; 0 = never steps.      */
        /*     iCycleMs   : ms per screen when cycling; 0 = no cycling.       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : repaints, records the schedule, arms its deadlines.     */
        /*--------------------------------------------------------------------*/

        public void TS_Start(enmScreen screen, int iAnimateMs, int iCycleMs)
        {
            int iNow;                            // this moment, in TickCount ms

            iNow = Environment.TickCount;

            lock (TS_fb.FB_SyncRoot())
            {   /* The schedule and the first paint change together. */
                TS_screen     = screen;
                TS_iAnimateMs = iAnimateMs;
                TS_iCycleMs   = iCycleMs;

                /* Both deadlines are armed from now; the one that does not    */
                /* apply is never consulted (TS_NextEventMs checks periods).   */
                TS_iNextStepTick   = unchecked(iNow + iAnimateMs);
                TS_iNextSwitchTick = unchecked(iNow + iCycleMs);
            }

            /* The first screen goes up at once (TS_FillScreen takes the lock itself). */
            TS_FillScreen(screen);
        }

        /*--------------------------------------------------------------------*/
        /* TS_NextEventMs:                                                    */
        /*                                                                    */
        /* Tells the server how long it may wait for a client message before  */
        /* the schedule needs TS_Tick: the time to the nearer of the gradient */
        /* step (only while the gradient is up) and the screen switch (only   */
        /* when cycling), never less than one millisecond, or -1 when neither */
        /* applies and the server may wait for ever.  This is the server's    */
        /* registered next-event callback.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : ms to wait (>= 1), or -1 for no scheduled event.         */
        /*--------------------------------------------------------------------*/

        public int TS_NextEventMs()
        {
            int  iNow;                           // this moment, in TickCount ms
            int  iWait;                          // the answer being assembled
            int  iLeft;                          // ms to one deadline (negative = overdue)
            bool bAny;                           // some deadline applies

            iNow  = Environment.TickCount;
            iWait = 0;
            bAny  = false;

            lock (TS_fb.FB_SyncRoot())
            {   /* Read the schedule consistently. */
                if ((TS_screen == enmScreen.ScreenGradient) && (TS_iAnimateMs > 0))
                {   /* The gradient is up and steps: its deadline counts. */
                    iLeft = unchecked(TS_iNextStepTick - iNow);
                    iWait = iLeft;
                    bAny  = true;
                }

                if (TS_iCycleMs > 0)
                {   /* Cycling: the switch deadline counts, and the nearer one wins. */
                    iLeft = unchecked(TS_iNextSwitchTick - iNow);

                    if (!bAny || (iLeft < iWait))
                    {   /* First or nearer deadline. */
                        iWait = iLeft;
                    }

                    bAny = true;
                }
            }

            if (!bAny)
            {   /* Nothing scheduled: a static screen for good. */
                return(-1);
            }

            if (iWait < 1)
            {   /* Overdue or due now: the smallest wait a read timeout accepts. */
                iWait = 1;
            }

            /* Time to the next event. */
            return(iWait);
        }

        /*--------------------------------------------------------------------*/
        /* TS_Tick:                                                           */
        /*                                                                    */
        /* Performs whatever the schedule has due: moves to the next screen   */
        /* when cycling and its time is up (repainting it, the last marker on */
        /* top), and steps the gradient when it is up and its period has      */
        /* elapsed.  An early call does nothing, so the caller may call it    */
        /* whenever its wait ends without checking why.  Deadlines advance by */
        /* their period from when they fell due, so no drift accumulates.     */
        /* This is the server's registered tick callback.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : may repaint and mark dirty; may advance the deadlines.  */
        /*--------------------------------------------------------------------*/

        public void TS_Tick()
        {
            int       iNow;                      // this moment, in TickCount ms
            bool      bSwitch;                   // a screen switch fell due
            bool      bStep;                     // a gradient step fell due
            enmScreen next;                      // the screen to switch to

            iNow    = Environment.TickCount;
            bSwitch = false;
            bStep   = false;
            next    = enmScreen.ScreenPattern;

            lock (TS_fb.FB_SyncRoot())
            {   /* Decide under the lock; paint after it, since the fills lock too. */
                if ((TS_iCycleMs > 0) && (unchecked(iNow - TS_iNextSwitchTick) >= 0))
                {   /* Switch due: the next screen in enmScreen order, wrapping. */
                    next               = (enmScreen)((((int)TS_screen) + 1) % TS_CYCLE_COUNT);
                    TS_screen          = next;
                    TS_iNextSwitchTick = unchecked(TS_iNextSwitchTick + TS_iCycleMs);
                    TS_iNextStepTick   = unchecked(iNow + TS_iAnimateMs);
                    bSwitch            = true;
                }

                if ((TS_screen == enmScreen.ScreenGradient) && (TS_iAnimateMs > 0) &&
                    (unchecked(iNow - TS_iNextStepTick) >= 0))
                {   /* Step due while the gradient is up. */
                    TS_iNextStepTick = unchecked(TS_iNextStepTick + TS_iAnimateMs);
                    bStep            = true;
                }
            }

            if (bSwitch)
            {   /* Bring the next screen up; a gradient's first frame is its step. */
                TS_FillScreen(next);

                lock (TS_fb.FB_SyncRoot())
                {   /* Keep the last touch visible on the new screen. */
                    if (TS_bHaveMarker)
                    {   /* Re-stamp; the fill already marked the buffer dirty. */
                        TS_PaintMarker(TS_iMarkerX, TS_iMarkerY);
                    }
                }

                /* The switch painted the gradient's first frame if that is what came up. */
                return;
            }

            if (bStep)
            {   /* One more frame of the moving gradient. */
                TS_AnimateStep();
            }
        }

        /*--------------------------------------------------------------------*/
        /* TS_Describe:                                                       */
        /*                                                                    */
        /* One phrase for the server's startup banner saying what the client  */
        /* will be shown: the single screen (with the gradient's period), or  */
        /* the cycle with its dwell, first screen and gradient period.  This  */
        /* is the server's registered describe callback.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : a lower-case description, no leading punctuation.     */
        /*--------------------------------------------------------------------*/

        public string TS_Describe()
        {
            string sText;                        // the phrase being assembled

            lock (TS_fb.FB_SyncRoot())
            {   /* Read the schedule consistently. */
                if (TS_iCycleMs > 0)
                {   /* Cycling through every screen. */
                    sText = "cycling all screens, " + (TS_iCycleMs / 1000) + " s each, starting with " +
                            TS_ScreenName(TS_screen) + " (gradient every " + TS_iAnimateMs + " ms)";
                }
                else if ((TS_screen == enmScreen.ScreenGradient) && (TS_iAnimateMs > 0))
                {   /* The gradient alone: name its period. */
                    sText = TS_ScreenName(TS_screen) + " every " + TS_iAnimateMs + " ms";
                }
                else
                {   /* One static screen. */
                    sText = TS_ScreenName(TS_screen);
                }
            }

            /* The phrase, ready to follow the geometry in the banner. */
            return(sText);
        }

        /*----------------------------------------------------------------------*/
        /* TS_DrawMarker:                                                       */
        /*                                                                      */
        /* Paints a small white square centred at (iCx, iCy), clipped to the    */
        /* image.  This is the server's registered pointer callback, invoked    */
        /* on a PointerEvent so a test can send a touch and then confirm the    */
        /* pixels changed there - proving the client->server direction          */
        /* round-trips.  The button mask is not used; every pointer event marks */
        /* except on the black screen, which stays black.                       */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     iCx   : centre x in pixels.                                      */
        /*     iCy   : centre y in pixels.                                      */
        /*     iMask : button mask (unused; a tap marks regardless).            */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : paints up to a 7x7 white block and marks dirty.           */
        /*----------------------------------------------------------------------*/

        public void TS_DrawMarker(int iCx, int iCy, int iMask)
        {
            lock (TS_fb.FB_SyncRoot())
            {   /* Paint, remember where (so animation can keep it), and flag the change. */
                if (TS_screen == enmScreen.ScreenBlack)
                {   /* The black screen stays black: a tap is logged by the server, not painted. */
                    return;
                }

                TS_PaintMarker(iCx, iCy);

                TS_bHaveMarker = true;
                TS_iMarkerX    = iCx;
                TS_iMarkerY    = iCy;
                TS_fb.FB_MarkDirty();
            }
        }

        /*--------------------------------------------------------------------*/
        /* TS_FillScreen:                                                     */
        /*                                                                    */
        /* Paints one screen: the three static screens as they are, the       */
        /* gradient at its current phase (TS_Tick then advances it).  Called  */
        /* by TS_Start for the first screen and by TS_Tick at each switch; an */
        /* unknown value paints the pattern, so the server always has         */
        /* something deterministic to serve.  Local.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     screen : which test screen to paint.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : repaints every pixel and marks the buffer dirty.        */
        /*--------------------------------------------------------------------*/

        private void TS_FillScreen(enmScreen screen)
        {
            if (screen == enmScreen.ScreenGradient)
            {   /* Animated gradient: one frame at the current phase; the ticks take it from there. */
                TS_AnimateStep();
            }
            else if (screen == enmScreen.ScreenBars)
            {   /* Colour bars: red, green, blue, left to right. */
                TS_FillColorBars();
            }
            else if (screen == enmScreen.ScreenGeometry)
            {   /* Geometry figure: border, square, circle, crosshair, ticks. */
                TS_FillGeometry();
            }
            else if (screen == enmScreen.ScreenBlack)
            {   /* Every pixel black. */
                TS_FillBlack();
            }
            else
            {   /* The static pattern, also the answer to any value this file does not know. */
                TS_FillTestPattern();
            }
        }

        /*--------------------------------------------------------------------*/
        /* TS_ScreenName:                                                     */
        /*                                                                    */
        /* Names a screen for the server's startup banner, so a log line says */
        /* which picture the client was being served.  Static, no state.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     screen : the test screen.                                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : a short lower-case description of the screen.         */
        /*--------------------------------------------------------------------*/

        private static string TS_ScreenName(enmScreen screen)
        {
            if (screen == enmScreen.ScreenGradient)
            {   /* The only screen that moves. */
                return("animated gradient");
            }

            if (screen == enmScreen.ScreenBars)
            {   /* Three bars in channel order. */
                return("colour bars R:G:B");
            }

            if (screen == enmScreen.ScreenGeometry)
            {   /* The square-and-circle figure. */
                return("geometry figure");
            }

            if (screen == enmScreen.ScreenBlack)
            {   /* Nothing lit. */
                return("black screen");
            }

            /* Everything else is the deterministic static pattern. */
            return("static pattern");
        }

        /*----------------------------------------------------------------------*/
        /* TS_FillTestPattern:                                                  */
        /*                                                                      */
        /* Paints a deterministic pattern: red rises with x, green rises with   */
        /* y, blue is a constant.  Every pixel's colour is a known function of  */
        /* its position, so a test can assert an exact value at any coordinate. */
        /* Local.                                                               */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     None.                                                            */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : overwrites every pixel and marks the buffer dirty.        */
        /*----------------------------------------------------------------------*/

        private void TS_FillTestPattern()
        {
            byte[] abPix;                        // the framebuffer's pixel store
            int    iW;                           // image width
            int    iH;                           // image height
            int    iOff;                         // byte offset of the current pixel
            int    x;
            int    y;

            abPix = TS_fb.FB_Buffer();
            iW    = TS_fb.FB_Width();
            iH    = TS_fb.FB_Height();

            lock (TS_fb.FB_SyncRoot())
            {   /* Hold the lock for the whole repaint. */
                for (y = 0; y < iH; y++)
                {   /* One row at a time. */
                    for (x = 0; x < iW; x++)
                    {   /* Colour is a pure function of (x, y). */
                        iOff = ((y * iW) + x) * 4;

                        abPix[iOff + 0] = (byte)(x & 0xFF);   // red
                        abPix[iOff + 1] = (byte)(y & 0xFF);   // green
                        abPix[iOff + 2] = 0x40;               // blue (constant)
                        abPix[iOff + 3] = 0;                  // pad
                    }
                }

                TS_fb.FB_MarkDirty();
            }
        }

        /*--------------------------------------------------------------------*/
        /* TS_FillColorBars:                                                  */
        /*                                                                    */
        /* Paints three full-height vertical bars of pure red, pure green and */
        /* pure blue, in that order from the left.  On the panel the order    */
        /* must read R, G, B: a display whose bytes are in the wrong order    */
        /* shows the bars in a different sequence, so this screen proves the  */
        /* client's pixel-format negotiation and the framebuffer's channel    */
        /* layout at a glance.  The width is split in three; any remainder    */
        /* widens the blue bar so every column is painted.  Local.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : overwrites every pixel and marks the buffer dirty.      */
        /*--------------------------------------------------------------------*/

        private void TS_FillColorBars()
        {
            byte[] abPix;                        // the framebuffer's pixel store
            int    iW;                           // image width
            int    iH;                           // image height
            int    iOff;                         // byte offset of the current pixel
            int    iBarWidth;                    // columns per bar; the blue bar takes the remainder
            int    iBar;                         // 0 red, 1 green, 2 blue for the current column
            byte   bRed;                         // the column's colour, chosen once per column
            byte   bGreen;
            byte   bBlue;
            int    x;
            int    y;

            abPix     = TS_fb.FB_Buffer();
            iW        = TS_fb.FB_Width();
            iH        = TS_fb.FB_Height();
            iBarWidth = iW / 3;

            if (iBarWidth < 1)
            {   /* Narrower than three columns: everything falls in the blue bar. */
                iBarWidth = 1;
            }

            lock (TS_fb.FB_SyncRoot())
            {   /* Hold the lock for the whole repaint. */
                for (x = 0; x < iW; x++)
                {   /* Decide the column's bar once, then paint it top to bottom. */
                    iBar = x / iBarWidth;

                    if (iBar == 0)
                    {   /* Left third: pure red. */
                        bRed   = 255;
                        bGreen = 0;
                        bBlue  = 0;
                    }
                    else if (iBar == 1)
                    {   /* Middle third: pure green. */
                        bRed   = 0;
                        bGreen = 255;
                        bBlue  = 0;
                    }
                    else
                    {   /* Right third, plus any remainder columns: pure blue. */
                        bRed   = 0;
                        bGreen = 0;
                        bBlue  = 255;
                    }

                    for (y = 0; y < iH; y++)
                    {   /* Every row of this column carries the bar's colour. */
                        iOff = ((y * iW) + x) * 4;

                        abPix[iOff + 0] = bRed;
                        abPix[iOff + 1] = bGreen;
                        abPix[iOff + 2] = bBlue;
                        abPix[iOff + 3] = 0;                  // pad
                    }
                }

                TS_fb.FB_MarkDirty();
            }
        }

        /*--------------------------------------------------------------------*/
        /* TS_FillGeometry:                                                   */
        /*                                                                    */
        /* Paints a figure whose every element has a known place, so the      */
        /* panel can be checked for stride, offset, aspect and touch mapping  */
        /* by eye: a one-pixel white border on all four edges (a missing or   */
        /* shifted side means a stride or offset error); a grey crosshair     */
        /* through the centre; short white ticks at the quarter points of     */
        /* every edge; a green square centred on the crosshair, its side the  */
        /* shorter panel dimension less a margin; and a red circle inscribed  */
        /* in that square (an ellipse means the aspect is wrong).  A tap on   */
        /* the centre or on a tick puts the white marker exactly on a known   */
        /* mark, checking the touch mapping numerically.  Black elsewhere.    */
        /*                                                                    */
        /* The circle is a one-pixel ring: a pixel is on it when its distance */
        /* from the centre is within half a pixel of the radius, tested in    */
        /* integers as (2r-1)^2 <= 4*d^2 <= (2r+1)^2.  Local.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : overwrites every pixel and marks the buffer dirty.      */
        /*--------------------------------------------------------------------*/

        private void TS_FillGeometry()
        {
            byte[] abPix;                        // the framebuffer's pixel store
            int    iW;                           // image width
            int    iH;                           // image height
            int    iOff;                         // byte offset of the current pixel
            int    iCx;                          // centre column
            int    iCy;                          // centre row
            int    iHalf;                        // half the square's side = the circle's radius
            int    iLeft;                        // square edges, inclusive
            int    iRight;
            int    iTop;
            int    iBottom;
            int    iTick;                        // tick length in pixels
            int    iDx;                          // pixel offset from the centre
            int    iDy;
            int    iD2x4;                        // 4 * squared distance from the centre
            int    iInner;                       // (2r - 1)^2, the ring's inner bound on iD2x4
            int    iOuter;                       // (2r + 1)^2, the ring's outer bound on iD2x4
            bool   bQuarterX;                    // this column is a quarter point of the width
            bool   bQuarterY;                    // this row is a quarter point of the height
            byte   bRed;                         // the pixel's colour, decided element by element
            byte   bGreen;
            byte   bBlue;
            int    x;
            int    y;

            abPix = TS_fb.FB_Buffer();
            iW    = TS_fb.FB_Width();
            iH    = TS_fb.FB_Height();

            /* The figure is centred; the square fits the shorter dimension with a margin. */
            iCx = iW / 2;
            iCy = iH / 2;

            if (iW < iH)
            {   /* Portrait: the width limits the square. */
                iHalf = (iW - (2 * TS_GEOMETRY_MARGIN)) / 2;
            }
            else
            {   /* Landscape or square: the height limits it. */
                iHalf = (iH - (2 * TS_GEOMETRY_MARGIN)) / 2;
            }

            if (iHalf < 1)
            {   /* Too small for a figure: degenerate to a single centre pixel. */
                iHalf = 1;
            }

            iLeft   = iCx - iHalf;
            iRight  = iCx + iHalf;
            iTop    = iCy - iHalf;
            iBottom = iCy + iHalf;
            iTick   = TS_GEOMETRY_TICK;
            iInner  = ((2 * iHalf) - 1) * ((2 * iHalf) - 1);
            iOuter  = ((2 * iHalf) + 1) * ((2 * iHalf) + 1);

            lock (TS_fb.FB_SyncRoot())
            {   /* Hold the lock for the whole repaint. */
                for (y = 0; y < iH; y++)
                {   /* One row at a time. */
                    bQuarterY = ((y == (iH / 4)) || (y == iCy) || (y == ((iH * 3) / 4)));

                    for (x = 0; x < iW; x++)
                    {   /* Decide the pixel from the back element forward; the last match wins. */
                        bQuarterX = ((x == (iW / 4)) || (x == iCx) || (x == ((iW * 3) / 4)));

                        /* Background: opaque black. */
                        bRed   = 0;
                        bGreen = 0;
                        bBlue  = 0;

                        if ((x == iCx) || (y == iCy))
                        {   /* Crosshair through the centre, full width and height, mid grey. */
                            bRed   = 128;
                            bGreen = 128;
                            bBlue  = 128;
                        }

                        if ((x == 0) || (y == 0) || (x == (iW - 1)) || (y == (iH - 1)))
                        {   /* The one-pixel border: white. */
                            bRed   = 255;
                            bGreen = 255;
                            bBlue  = 255;
                        }

                        if ((bQuarterX && ((y < iTick) || (y >= (iH - iTick)))) ||
                            (bQuarterY && ((x < iTick) || (x >= (iW - iTick)))))
                        {   /* Quarter-point ticks along every edge: white. */
                            bRed   = 255;
                            bGreen = 255;
                            bBlue  = 255;
                        }

                        if ((((x == iLeft) || (x == iRight)) && (y >= iTop) && (y <= iBottom)) ||
                            (((y == iTop) || (y == iBottom)) && (x >= iLeft) && (x <= iRight)))
                        {   /* The square's outline: green. */
                            bRed   = 0;
                            bGreen = 255;
                            bBlue  = 0;
                        }

                        iDx   = x - iCx;
                        iDy   = y - iCy;
                        iD2x4 = 4 * ((iDx * iDx) + (iDy * iDy));

                        if ((iD2x4 >= iInner) && (iD2x4 <= iOuter))
                        {   /* On the inscribed circle's one-pixel ring: red, on top of everything. */
                            bRed   = 255;
                            bGreen = 0;
                            bBlue  = 0;
                        }

                        iOff = ((y * iW) + x) * 4;

                        abPix[iOff + 0] = bRed;
                        abPix[iOff + 1] = bGreen;
                        abPix[iOff + 2] = bBlue;
                        abPix[iOff + 3] = 0;                  // pad
                    }
                }

                TS_fb.FB_MarkDirty();
            }
        }

        /*--------------------------------------------------------------------*/
        /* TS_FillBlack:                                                      */
        /*                                                                    */
        /* Paints every pixel black.  This is the one screen with no colour   */
        /* anywhere, which is what the client's black-frame backlight rule    */
        /* waits for; TS_DrawMarker leaves it black under taps so a finger on */
        /* the dark panel does not itself light the frame.  It is not part of */
        /* the cycle.  Local.                                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : overwrites every pixel and marks the buffer dirty.      */
        /*--------------------------------------------------------------------*/

        private void TS_FillBlack()
        {
            byte[] abPix;                        // the framebuffer's pixel store
            int    i;

            abPix = TS_fb.FB_Buffer();

            lock (TS_fb.FB_SyncRoot())
            {   /* Hold the lock for the whole repaint. */
                for (i = 0; i < abPix.Length; i++)
                {   /* Every channel and the pad byte to zero. */
                    abPix[i] = 0;
                }

                TS_fb.FB_MarkDirty();
            }
        }

        /*---------------------------------------------------------------------*/
        /* TS_PaintMarker:                                                     */
        /*                                                                     */
        /* Paints the 7x7 white marker block centred at (iCx, iCy), clipped to */
        /* the image.  The CALLER holds the framebuffer lock; this neither     */
        /* locks nor marks dirty, so TS_DrawMarker and TS_AnimateStep can both */
        /* use it.  Local.                                                     */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     iCx : centre x in pixels.                                       */
        /*     iCy : centre y in pixels.                                       */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : paints up to 49 pixels white.                            */
        /*---------------------------------------------------------------------*/

        private void TS_PaintMarker(int iCx, int iCy)
        {
            byte[] abPix;                        // the framebuffer's pixel store
            int    iW;                           // image width
            int    iH;                           // image height
            int    iOff;                         // byte offset of the current pixel
            int    x;
            int    y;

            abPix = TS_fb.FB_Buffer();
            iW    = TS_fb.FB_Width();
            iH    = TS_fb.FB_Height();

            for (y = iCy - 3; y <= iCy + 3; y++)
            {   /* Skip rows outside the image. */
                if ((y < 0) || (y >= iH))
                {   /* Off-image row. */
                    continue;
                }

                for (x = iCx - 3; x <= iCx + 3; x++)
                {   /* Skip columns outside the image. */
                    if ((x < 0) || (x >= iW))
                    {   /* Off-image column. */
                        continue;
                    }

                    iOff = ((y * iW) + x) * 4;
                    abPix[iOff + 0] = 255;   // white marker
                    abPix[iOff + 1] = 255;
                    abPix[iOff + 2] = 255;
                    abPix[iOff + 3] = 0;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* TS_AnimateStep:                                                    */
        /*                                                                    */
        /* Paints the gradient at the current phase and advances the phase,   */
        /* so every pixel of the client's framebuffer changes on every step   */
        /* and page flipping, cache flushing and alpha forcing are exercised  */
        /* continuously.  The last pointer marker is stamped back on top so a */
        /* touch stays visible.  Each channel moves at its own rate: red      */
        /* slides along x, green along y, blue along the diagonal, so the     */
        /* bands visibly crawl in three directions and a stuck or torn frame  */
        /* is obvious at a glance.  Called by TS_FillScreen for the first     */
        /* frame and by TS_Tick for every step after it.  Local.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : repaints every pixel, advances the phase, marks dirty.  */
        /*--------------------------------------------------------------------*/

        private void TS_AnimateStep()
        {
            byte[] abPix;                        // the framebuffer's pixel store
            int    iW;                           // image width
            int    iH;                           // image height
            int    iOff;                         // byte offset of the current pixel
            int    iPhase;                       // this step's phase, read once
            int    x;
            int    y;

            abPix = TS_fb.FB_Buffer();
            iW    = TS_fb.FB_Width();
            iH    = TS_fb.FB_Height();

            lock (TS_fb.FB_SyncRoot())
            {   /* Hold the lock for the whole repaint so no client tears a row. */
                iPhase = TS_iPhase;

                for (y = 0; y < iH; y++)
                {   /* One row at a time. */
                    for (x = 0; x < iW; x++)
                    {   /* Colour is a function of (x, y) and the phase; each channel drifts differently. */
                        iOff = ((y * iW) + x) * 4;

                        abPix[iOff + 0] = (byte)((x + iPhase) & 0xFF);         // red slides right
                        abPix[iOff + 1] = (byte)((y + (iPhase * 2)) & 0xFF);   // green slides down, twice as fast
                        abPix[iOff + 2] = (byte)((x + y - iPhase) & 0xFF);     // blue slides up the diagonal
                        abPix[iOff + 3] = 0;                                   // pad
                    }
                }

                if (TS_bHaveMarker)
                {   /* Keep the last touch visible on top of the moving background. */
                    TS_PaintMarker(TS_iMarkerX, TS_iMarkerY);
                }

                /* Next step moves everything one more pixel; the wrap keeps the phase small. */
                TS_iPhase = (iPhase + 1) & 0xFFFF;
                TS_fb.FB_MarkDirty();
            }
        }
    }
}
