/*----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* Licensed under the GNU General Public License version 2 or later --        */
/* see LICENSE beside this source.  GPL-2.0 full text in GPL-2.0.txt.         */
/*----------------------------------------------------------------------------*/
/* PixelFormat.cs:                                                            */
/*                                                                            */
/* The RFB PIXEL_FORMAT (RFC 6143 section 7.4), plus the encode step that     */
/* turns an internal 8-bit-per-channel RGB pixel into whatever packed format  */
/* the connected client asked for.  The server stores its framebuffer in      */
/* plain R,G,B bytes and encodes on the way out, so it can honour any client  */
/* SetPixelFormat without keeping the framebuffer in that format.             */
/*                                                                            */
/* Self-contained and dependency-free (mscorlib only), so it merges into any  */
/* RFB server as a single file.  Pure C# 5.0: no interpolation, no            */
/* expression-bodied members.                                                 */
/*----------------------------------------------------------------------------*/

using System;

namespace PixelFormat
{
    /*------------------------------------------------------------------------*/
    /* clsPixelFormat:                                                        */
    /*                                                                        */
    /* Describes how one true-colour pixel is packed on the wire: byte count, */
    /* endianness, and each channel's maximum value and bit shift.  Mirrors   */
    /* the RFB PIXEL_FORMAT structure of RFC 6143 section 7.4.                */
    /*------------------------------------------------------------------------*/

    public sealed class clsPixelFormat
    {
        public int  PIXF_iBytesPerPixel;        // 1, 2 or 4 bytes per pixel
        public int  PIXF_iDepth;                // significant colour bits
        public bool PIXF_bBigEndian;            // true: multi-byte pixel is big-endian

        public int  PIXF_iRedMax;               // (1 << red_bits)   - 1
        public int  PIXF_iGreenMax;             // (1 << green_bits) - 1
        public int  PIXF_iBlueMax;              // (1 << blue_bits)  - 1

        public int  PIXF_iRedShift;             // left shift of red within the pixel word
        public int  PIXF_iGreenShift;           // left shift of green
        public int  PIXF_iBlueShift;            // left shift of blue

        /*---------------------------------------------------------------------*/
        /* PIXF_CreateDefault:                                                 */
        /*                                                                     */
        /* Builds the server's advertised default format: 32-bit little-endian */
        /* true colour, 8 bits per channel, red high (0x00RRGGBB).  The client */
        /* will normally override it with SetPixelFormat, but ServerInit must  */
        /* advertise something valid.                                          */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     clsPixelFormat : a fresh 32bpp RGB little-endian format.        */
        /*---------------------------------------------------------------------*/

        public static clsPixelFormat PIXF_CreateDefault()
        {
            clsPixelFormat fmt;                   // format being built

            fmt = new clsPixelFormat();

            /* The default clients most often accept unchanged: 32-bit little-   */
            /* endian 0x00RRGGBB, 8 significant bits per channel, so the first   */
            /* frame usually needs no conversion.                                */
            fmt.PIXF_iBytesPerPixel = 4;
            fmt.PIXF_iDepth         = 24;
            fmt.PIXF_bBigEndian     = false;
            fmt.PIXF_iRedMax        = 255;
            fmt.PIXF_iGreenMax      = 255;
            fmt.PIXF_iBlueMax       = 255;
            fmt.PIXF_iRedShift      = 16;
            fmt.PIXF_iGreenShift    = 8;
            fmt.PIXF_iBlueShift     = 0;

            /* Hand back the populated default. */
            return(fmt);
        }

        /*--------------------------------------------------------------------*/
        /* PIXF_WriteWire:                                                    */
        /*                                                                    */
        /* Serialises this format into the 16-byte RFB PIXEL_FORMAT layout at */
        /* the given offset (used inside ServerInit).                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abDst : destination buffer, at least iOff + 16 bytes.          */
        /*     iOff  : offset to write at.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : writes 16 bytes into abDst.                             */
        /*--------------------------------------------------------------------*/

        public void PIXF_WriteWire(byte[] abDst, int iOff)
        {
            /* bits-per-pixel, depth, big-endian flag, true-colour flag. */
            abDst[iOff + 0] = (byte)(PIXF_iBytesPerPixel * 8);
            abDst[iOff + 1] = (byte)PIXF_iDepth;
            abDst[iOff + 2] = (byte)(PIXF_bBigEndian ? 1 : 0);
            abDst[iOff + 3] = 1;                 // true colour (no palette)

            /* Channel maxima, big-endian u16 each. */
            abDst[iOff + 4] = (byte)((PIXF_iRedMax   >> 8) & 0xFF);
            abDst[iOff + 5] = (byte)(PIXF_iRedMax    & 0xFF);
            abDst[iOff + 6] = (byte)((PIXF_iGreenMax >> 8) & 0xFF);
            abDst[iOff + 7] = (byte)(PIXF_iGreenMax  & 0xFF);
            abDst[iOff + 8] = (byte)((PIXF_iBlueMax  >> 8) & 0xFF);
            abDst[iOff + 9] = (byte)(PIXF_iBlueMax   & 0xFF);

            /* Channel shifts, one byte each. */
            abDst[iOff + 10] = (byte)PIXF_iRedShift;
            abDst[iOff + 11] = (byte)PIXF_iGreenShift;
            abDst[iOff + 12] = (byte)PIXF_iBlueShift;

            /* Three padding bytes. */
            abDst[iOff + 13] = 0;
            abDst[iOff + 14] = 0;
            abDst[iOff + 15] = 0;
        }

        /*--------------------------------------------------------------------*/
        /* PIXF_ReadWire:                                                     */
        /*                                                                    */
        /* Parses a 16-byte RFB PIXEL_FORMAT (from a client SetPixelFormat)   */
        /* into a new clsPixelFormat.  Only true-colour is supported; a       */
        /* palette-mode request is accepted structurally but treated as its   */
        /* RGB fields, since this server never sends colour-map entries.      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abSrc : source buffer.                                         */
        /*     iOff  : offset of the 16-byte format.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     clsPixelFormat : the parsed format.                            */
        /*--------------------------------------------------------------------*/

        public static clsPixelFormat PIXF_ReadWire(byte[] abSrc, int iOff)
        {
            clsPixelFormat fmt;                   // parsed format

            fmt = new clsPixelFormat();

            /* Header fields: bytes-per-pixel (from the bit count), depth, endianness. */
            fmt.PIXF_iBytesPerPixel = abSrc[iOff + 0] / 8;
            fmt.PIXF_iDepth         = abSrc[iOff + 1];
            fmt.PIXF_bBigEndian     = (abSrc[iOff + 2] != 0);

            /* Channel maxima, big-endian u16 each. */
            fmt.PIXF_iRedMax   = ((abSrc[iOff + 4] & 0xFF) << 8) | (abSrc[iOff + 5] & 0xFF);
            fmt.PIXF_iGreenMax = ((abSrc[iOff + 6] & 0xFF) << 8) | (abSrc[iOff + 7] & 0xFF);
            fmt.PIXF_iBlueMax  = ((abSrc[iOff + 8] & 0xFF) << 8) | (abSrc[iOff + 9] & 0xFF);

            /* Channel shifts, one byte each. */
            fmt.PIXF_iRedShift   = abSrc[iOff + 10];
            fmt.PIXF_iGreenShift = abSrc[iOff + 11];
            fmt.PIXF_iBlueShift  = abSrc[iOff + 12];

            /* Guard against a nonsensical byte count so encoding stays in bounds. */
            if ((fmt.PIXF_iBytesPerPixel < 1) || (fmt.PIXF_iBytesPerPixel > 4))
            {   /* Fall back to a 4-byte pixel rather than trusting a bad value. */
                fmt.PIXF_iBytesPerPixel = 4;
            }

            /* Hand back the parsed format. */
            return(fmt);
        }

        /*----------------------------------------------------------------------*/
        /* PIXF_ScaleChannel:                                                   */
        /*                                                                      */
        /* Rescales an 8-bit colour component (0..255) to this format's channel */
        /* width, rounded.  A zero max (channel absent) yields 0.               */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     iComp8 : the source component, 0..255.                           */
        /*     iMax   : the destination channel maximum.                        */
        /*                                                                      */
        /* Returns:                                                             */
        /*     int : the rescaled component, 0..iMax.                           */
        /*----------------------------------------------------------------------*/

        private static int PIXF_ScaleChannel(int iComp8, int iMax)
        {
            if (iMax <= 0)
            {   /* No bits for this channel in the destination format. */
                return(0);
            }

            if (iMax == 255)
            {   /* Same width: no scaling needed (the common 8-bit case). */
                return(iComp8);
            }

            /* Rounded rescale from 0..255 to 0..iMax. */
            return(((iComp8 * iMax) + 127) / 255);
        }

        /*----------------------------------------------------------------------*/
        /* PIXF_EncodePixel:                                                    */
        /*                                                                      */
        /* Packs an 8-bit RGB triple into this format and writes it to abDst at */
        /* iOff, honouring byte count and endianness.  This is the one place    */
        /* the server's internal RGB storage meets the client's chosen format.  */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     iR    : red   0..255.                                            */
        /*     iG    : green 0..255.                                            */
        /*     iB    : blue  0..255.                                            */
        /*     abDst : destination buffer.                                      */
        /*     iOff  : offset to write at.                                      */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : writes PIXF_iBytesPerPixel bytes.                         */
        /*----------------------------------------------------------------------*/

        public void PIXF_EncodePixel(int iR, int iG, int iB, byte[] abDst, int iOff)
        {
            int iVal;                            // assembled pixel word
            int i;

            /* Rescale each channel and place it at its shift. */
            iVal = (PIXF_ScaleChannel(iR, PIXF_iRedMax)   << PIXF_iRedShift)   |
                   (PIXF_ScaleChannel(iG, PIXF_iGreenMax) << PIXF_iGreenShift) |
                   (PIXF_ScaleChannel(iB, PIXF_iBlueMax)  << PIXF_iBlueShift);

            if (PIXF_bBigEndian)
            {   /* Big-endian: most significant byte first. */
                for (i = PIXF_iBytesPerPixel - 1; i >= 0; i--)
                {   /* Emit byte i at position i within the pixel. */
                    abDst[iOff + i] = (byte)(iVal & 0xFF);
                    iVal = iVal >> 8;
                }
            }
            else
            {   /* Little-endian: least significant byte first. */
                for (i = 0; i < PIXF_iBytesPerPixel; i++)
                {   /* Peel the low byte off each pass. */
                    abDst[iOff + i] = (byte)(iVal & 0xFF);
                    iVal = iVal >> 8;
                }
            }
        }
    }
}
