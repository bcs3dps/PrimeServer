/*------------------------------------------------------------------------------*/
/* SPDX-License-Identifier: GPL-2.0-or-later                                    */
/* Copyright (c) 2026 B. C. Services                                            */
/*                                                                              */
/* Licensed under the GNU General Public License version 2 or later --          */
/* see LICENSE beside this source.  GPL-2.0 full text in GPL-2.0.txt.           */
/*------------------------------------------------------------------------------*/
/* Program.cs:                                                                  */
/*                                                                              */
/* Console entry point for PrimeServe: parse the command line, build a          */
/* synthetic framebuffer and an RFB server over it, and run.  Headless - there  */
/* is no local display; the framebuffer exists only to be served over VNC so    */
/* a VNC client can be exercised end to end without a real display or hardware. */
/*                                                                              */
/* CLI-first: everything is decided from argv before the server starts, and     */
/* --help / --version return immediately.  Pure C# 5.0.                         */
/*------------------------------------------------------------------------------*/

using System;
using FrameBuffer;
using RfbServer;
using TestScreens;

namespace PrimeServe
{
    /*------------------------------------------------------------------------*/
    /* clsProgram:                                                            */
    /*                                                                        */
    /* Holds Main and the argument parsing.  No instance state.               */
    /*------------------------------------------------------------------------*/

    internal static class clsProgram
    {
        /*--------------------------------------------------------------------*/
        /* PROG_Usage:                                                        */
        /*                                                                    */
        /* Prints usage to the given writer.  Local.                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bError : true to write to stderr (a usage error), else stdout. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : writes usage text.                                      */
        /*--------------------------------------------------------------------*/

        private static void PROG_Usage(bool bError)
        {
            System.IO.TextWriter w;              // chosen output stream

            if (bError)
            {   /* Usage errors go to stderr. */
                w = Console.Error;
            }
            else
            {   /* Explicit --help goes to stdout. */
                w = Console.Out;
            }

            w.WriteLine("PrimeServe - headless RFB (VNC) server over a synthetic framebuffer");
            w.WriteLine("");
            w.WriteLine("Usage: PrimeServe.exe [options]");
            w.WriteLine("  --port N            listen port (default 5900)");
            w.WriteLine("  --width N           framebuffer width (default 480)");
            w.WriteLine("  --height N          framebuffer height (default 800)");
            w.WriteLine("  --password P        VNC password (default: none / No-auth)");
            w.WriteLine("  --password-env NAME read the VNC password from env var NAME");
            w.WriteLine("  --screen NAME       test screen: pattern | gradient | bars | geometry | black");
            w.WriteLine("                      (default pattern; --animate alone selects gradient;");
            w.WriteLine("                      black is not in the cycle)");
            w.WriteLine("  --animate MS        gradient step period in ms (default 100 with --screen gradient;");
            w.WriteLine("                      only the gradient animates)");
            w.WriteLine("  --cycle S           show every screen in turn, S seconds each, starting with --screen");
            w.WriteLine("  --once              serve a single client, then exit (for tests)");
            w.WriteLine("  -v, --verbose       per-message diagnostic logging");
            w.WriteLine("  -h, --help          this help");
            w.WriteLine("  --version           print version and exit");
        }

        /*--------------------------------------------------------------------*/
        /* PROG_NextValue:                                                    */
        /*                                                                    */
        /* Returns the value following an option, or null (with an error      */
        /* message) if it is missing, advancing the caller's index.  Local.   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     asArgs : the argument array.                                   */
        /*     piPos  : in/out; index of the option, advanced to its value.   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the value, or null if none followed.                  */
        /*--------------------------------------------------------------------*/

        private static string PROG_NextValue(string[] asArgs, ref int piPos)
        {
            if ((piPos + 1) >= asArgs.Length)
            {   /* Option was last with nothing after it. */
                Console.Error.WriteLine("primeserve: option '" + asArgs[piPos] + "' needs a value");
                return(null);
            }

            /* Step onto the value that follows the option. */
            piPos = piPos + 1;

            /* Hand that value back to the caller. */
            return(asArgs[piPos]);
        }

        /*---------------------------------------------------------------------*/
        /* PROG_ParseInt:                                                      */
        /*                                                                     */
        /* Parses an option's value as an integer, printing a usage error and  */
        /* returning false if it is not one.  This replaces a bare int.Parse,  */
        /* which threw an unhandled exception - a stack-trace crash - on any   */
        /* non-numeric or out-of-range value the user typed; a CLI must answer */
        /* bad input with a message, not a crash.  Local.                      */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sVal  : the value string that followed the option.              */
        /*     sOpt  : the option name, for the error message.                 */
        /*     piOut : out; the parsed integer, or 0 when this returns false.  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool : true and piOut set on success; false (message already    */
        /*            printed) on a value that is not a valid integer.         */
        /*---------------------------------------------------------------------*/

        private static bool PROG_ParseInt(string sVal, string sOpt, out int piOut)
        {
            if (!int.TryParse(sVal, out piOut))
            {   /* Not an integer: name the option and the offending value. */
                Console.Error.WriteLine("primeserve: option '" + sOpt +
                                        "' needs an integer, got '" + sVal + "'");
                return(false);
            }

            /* A valid integer; piOut holds it. */
            return(true);
        }

        /*-----------------------------------------------------------------------*/
        /* Main:                                                                 */
        /*                                                                       */
        /* Program entry point.  Parses options, builds the framebuffer and      */
        /* server, and runs until the client disconnects (--once) or the process */
        /* is killed.                                                            */
        /*                                                                       */
        /* Arguments:                                                            */
        /*     asArgs : command-line arguments.                                  */
        /*                                                                       */
        /* Returns:                                                              */
        /*     int : 0 on a clean run, 1 on a usage error.                       */
        /*-----------------------------------------------------------------------*/

        public static int Main(string[] asArgs)
        {
            int    iPort;           // listen port
            int    iWidth;          // framebuffer width
            int    iHeight;         // framebuffer height
            string sPassword;       // VNC password ("" = none)
            bool   bOnce;           // serve one client and exit
            bool   bVerbose;        // diagnostic logging
            int      iAnimateMs;    // gradient step period; 0 = a static screen
            int      iCycleS;       // seconds per screen when cycling; 0 = one screen for good
            enmScreen screen;       // the test screen to paint and serve (the first one when cycling)
            bool     bScreenGiven;  // --screen was on the command line
            int      i;

            /* Defaults. */
            iPort        = 5900;
            iWidth       = 480;
            iHeight      = 800;
            sPassword    = "";
            bOnce        = false;
            bVerbose     = false;
            iAnimateMs   = 0;
            iCycleS      = 0;
            screen       = enmScreen.ScreenPattern;
            bScreenGiven = false;

            for (i = 0; i < asArgs.Length; i++)
            {   /* Walk the options. */
                string sArg = asArgs[i];

                if ((sArg == "-h") || (sArg == "--help"))
                {   /* Help to stdout, clean exit. */
                    PROG_Usage(false);
                    return(0);
                }

                if (sArg == "--version")
                {   /* Version, clean exit. */
                    Console.Out.WriteLine("PrimeServe 0.3.0-dev");
                    return(0);
                }

                if ((sArg == "-v") || (sArg == "--verbose"))
                {   /* Enable diagnostics. */
                    bVerbose = true;
                    continue;
                }

                if (sArg == "--once")
                {   /* Single-client test mode. */
                    bOnce = true;
                    continue;
                }

                if (sArg == "--port")
                {   /* Listen port. */
                    string sVal = PROG_NextValue(asArgs, ref i);

                    if (sVal == null)
                    {   /* The option had no value; PROG_NextValue already said so. */
                        return(1);
                    }

                    if (!PROG_ParseInt(sVal, sArg, out iPort))
                    {   /* Not a number; PROG_ParseInt already said so. */
                        return(1);
                    }
                    continue;
                }

                if (sArg == "--width")
                {   /* Framebuffer width. */
                    string sVal = PROG_NextValue(asArgs, ref i);

                    if (sVal == null)
                    {   /* The option had no value; PROG_NextValue already said so. */
                        return(1);
                    }

                    if (!PROG_ParseInt(sVal, sArg, out iWidth))
                    {   /* Not a number; PROG_ParseInt already said so. */
                        return(1);
                    }
                    continue;
                }

                if (sArg == "--height")
                {   /* Framebuffer height. */
                    string sVal = PROG_NextValue(asArgs, ref i);

                    if (sVal == null)
                    {   /* The option had no value; PROG_NextValue already said so. */
                        return(1);
                    }

                    if (!PROG_ParseInt(sVal, sArg, out iHeight))
                    {   /* Not a number; PROG_ParseInt already said so. */
                        return(1);
                    }
                    continue;
                }

                if (sArg == "--password")
                {   /* VNC password on the command line (test convenience). */
                    string sVal = PROG_NextValue(asArgs, ref i);

                    if (sVal == null)
                    {   /* The option had no value; PROG_NextValue already said so. */
                        return(1);
                    }

                    sPassword = sVal;
                    continue;
                }

                if (sArg == "--animate")
                {   /* Animated test screen: the step period in milliseconds. */
                    string sVal = PROG_NextValue(asArgs, ref i);

                    if (sVal == null)
                    {   /* The option had no value; PROG_NextValue already said so. */
                        return(1);
                    }

                    if (!PROG_ParseInt(sVal, sArg, out iAnimateMs))
                    {   /* Not a number; PROG_ParseInt already said so. */
                        return(1);
                    }
                    continue;
                }

                if (sArg == "--screen")
                {   /* Which test screen to paint; the names are the enmScreen members in lower case. */
                    string sVal = PROG_NextValue(asArgs, ref i);

                    if (sVal == null)
                    {   /* The option had no value; PROG_NextValue already said so. */
                        return(1);
                    }

                    if (sVal == "pattern")
                    {   /* The static pattern the loopback test asserts. */
                        screen = enmScreen.ScreenPattern;
                    }
                    else if (sVal == "gradient")
                    {   /* The animated gradient. */
                        screen = enmScreen.ScreenGradient;
                    }
                    else if (sVal == "bars")
                    {   /* Red, green, blue bars for the channel order. */
                        screen = enmScreen.ScreenBars;
                    }
                    else if (sVal == "geometry")
                    {   /* The square-and-circle figure for stride, aspect and touch. */
                        screen = enmScreen.ScreenGeometry;
                    }
                    else if (sVal == "black")
                    {   /* Nothing lit, for the client's black-frame backlight rule. */
                        screen = enmScreen.ScreenBlack;
                    }
                    else
                    {   /* Not one of the five. */
                        Console.Error.WriteLine("primeserve: unknown screen '" + sVal +
                                                "' (pattern | gradient | bars | geometry | black)");
                        return(1);
                    }

                    bScreenGiven = true;
                    continue;
                }

                if (sArg == "--cycle")
                {   /* Cycle through every screen: the seconds each one stays up. */
                    string sVal = PROG_NextValue(asArgs, ref i);

                    if (sVal == null)
                    {   /* The option had no value; PROG_NextValue already said so. */
                        return(1);
                    }

                    if (!PROG_ParseInt(sVal, sArg, out iCycleS))
                    {   /* Not a number; PROG_ParseInt already said so. */
                        return(1);
                    }
                    continue;
                }

                if (sArg == "--password-env")
                {   /* VNC password from an environment variable. */
                    string sVal = PROG_NextValue(asArgs, ref i);

                    if (sVal == null)
                    {   /* The option had no value; PROG_NextValue already said so. */
                        return(1);
                    }

                    string sEnv = Environment.GetEnvironmentVariable(sVal);
                    if (sEnv == null)
                    {   /* Named variable not set. */
                        Console.Error.WriteLine("primeserve: env var '" + sVal + "' is not set");
                        return(1);
                    }
                    sPassword = sEnv;
                    continue;
                }

                /* Anything else is unrecognised. */
                Console.Error.WriteLine("primeserve: unknown option '" + sArg + "'");
                PROG_Usage(true);
                return(1);
            }

            if ((iWidth <= 0) || (iHeight <= 0))
            {   /* A zero or negative dimension cannot make a framebuffer. */
                Console.Error.WriteLine("primeserve: width and height must be positive");
                return(1);
            }

            if (iAnimateMs < 0)
            {   /* A negative period is meaningless; 0 is a static screen. */
                Console.Error.WriteLine("primeserve: --animate period must be 0 or more");
                return(1);
            }

            if (iCycleS < 0)
            {   /* A negative dwell is meaningless; 0 is no cycling. */
                Console.Error.WriteLine("primeserve: --cycle seconds must be 0 or more");
                return(1);
            }

            /* Reconcile the screen, the animation period and the cycle.     */
            /* Cycling shows every screen, so the period is the gradient's   */
            /* step during its turn and defaults to 100 ms.  Otherwise a     */
            /* period alone selects the gradient; the gradient with no       */
            /* period gets the usual 100 ms; and a period on a static        */
            /* screen is a contradiction, refused rather than ignored.       */
            if (iCycleS > 0)
            {   /* Cycling: the gradient needs a step period for its turn,   */
                /* and the black screen is not a cycle member, so starting   */
                /* from it is refused.                                       */
                if (screen == enmScreen.ScreenBlack)
                {   /* The cycle walks the four picture screens only. */
                    Console.Error.WriteLine("primeserve: --screen black is not in the cycle; drop --cycle or pick another screen");
                    return(1);
                }

                if (iAnimateMs == 0)
                {   /* None given: the default step. */
                    iAnimateMs = 100;
                }
            }
            else
            {   /* One screen for good. */
                if ((iAnimateMs > 0) && !bScreenGiven)
                {   /* --animate MS on its own: the animated gradient. */
                    screen = enmScreen.ScreenGradient;
                }

                if ((screen == enmScreen.ScreenGradient) && (iAnimateMs == 0))
                {   /* --screen gradient with no period: the default step. */
                    iAnimateMs = 100;
                }

                if ((screen != enmScreen.ScreenGradient) && (iAnimateMs > 0))
                {   /* A period was given for a screen that never changes. */
                    Console.Error.WriteLine("primeserve: --animate applies to --screen gradient only");
                    return(1);
                }
            }

            /* Build the framebuffer and the test-screen harness that        */
            /* paints it, then the server, and register the harness as the   */
            /* server's content driver.                                      */
            clsFrameBuffer fb  = new clsFrameBuffer(iWidth, iHeight);
            clsTestScreens ts  = new clsTestScreens(fb);
            ts.TS_Start(screen, iAnimateMs, iCycleS * 1000);
            clsRfbServer   srv = new clsRfbServer(iPort, sPassword, fb, bVerbose);
            srv.SRV_SetHooks(ts.TS_NextEventMs, ts.TS_Tick, ts.TS_Describe, ts.TS_DrawMarker);

            srv.SRV_Run(bOnce);

            /* A clean run (one client under --once, or process kill otherwise). */
            return(0);
        }
    }
}
