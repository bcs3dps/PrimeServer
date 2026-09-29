/*------------------------------------------------------------------------------*/
/* SPDX-License-Identifier: GPL-2.0-or-later                                    */
/* Copyright (c) 2026 B. C. Services                                            */
/*                                                                              */
/* Licensed under the GNU General Public License version 2 or later --          */
/* see LICENSE beside this source.  GPL-2.0 full text in GPL-2.0.txt.           */
/*------------------------------------------------------------------------------*/
/* FrameBuffer.cs:                                                              */
/*                                                                              */
/* A synthetic, headless framebuffer: a plain in-memory RGB image with a dirty  */
/* flag and a RAW encoder, and no knowledge of what is drawn into it or how     */
/* often.  It is the reusable core an RFB server serves; a consumer -- the test */
/* harness, or a real page renderer -- writes pixels through the write API and  */
/* marks the buffer dirty, and the server encodes rows out of it into whatever  */
/* pixel format the client negotiated.                                          */
/*                                                                              */
/* Pixels are stored one byte per channel (R, G, B, pad) so the value at any    */
/* coordinate is trivially checkable by a test.  Self-contained (mscorlib plus  */
/* the pixel-format unit), so it merges into another program as one file.       */
/*------------------------------------------------------------------------------*/

using System;
using PixelFormat;

namespace FrameBuffer
{
    /*--------------------------------------------------------------------------*/
    /* clsFrameBuffer:                                                          */
    /*                                                                          */
    /* Width x height RGB image plus a dirty flag and a RAW encoder.  A caller  */
    /* paints through FB_Buffer while holding FB_SyncRoot and then calls        */
    /* FB_MarkDirty; the server reads FB_TakeDirty and FB_EncodeRawRect.  One   */
    /* lock guards the pixels for both, so a paint and an encode never overlap. */
    /*--------------------------------------------------------------------------*/

    public sealed class clsFrameBuffer
    {
        private readonly int    FB_iWidth;           // image width in pixels
        private readonly int    FB_iHeight;          // image height in pixels
        private readonly byte[] FB_abPixels;         // width*height*4 bytes, {R,G,B,pad}
        private readonly object FB_oLock;            // guards pixels + dirty flag
        private bool            FB_bDirty;           // set when pixels changed since last send

        /*--------------------------------------------------------------------*/
        /* clsFrameBuffer (constructor):                                      */
        /*                                                                    */
        /* Allocates a black (all-zero) image.  Nothing is painted and the    */
        /* buffer is not dirty: a caller paints the first frame through the   */
        /* write API, which marks it dirty.                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iWidth  : image width in pixels (> 0).                         */
        /*     iHeight : image height in pixels (> 0).                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     clsFrameBuffer : an allocated, black, not-dirty framebuffer.   */
        /*--------------------------------------------------------------------*/

        public clsFrameBuffer(int iWidth, int iHeight)
        {
            /* Store the geometry, allocate the all-zero (black) image, and   */
            /* start clean: no paint has happened, so nothing is dirty yet.   */
            FB_iWidth   = iWidth;
            FB_iHeight  = iHeight;
            FB_abPixels = new byte[iWidth * iHeight * 4];
            FB_oLock    = new object();
            FB_bDirty   = false;
        }

        /*---------------------------------------------------------------------*/
        /* FB_Width:                                                           */
        /*                                                                     */
        /* The image width in pixels, fixed for the buffer's lifetime.  The    */
        /* server reads it for ServerInit and update rectangles, and a painter */
        /* reads it to size its writes.                                        */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     int : the width in pixels.                                      */
        /*---------------------------------------------------------------------*/

        public int FB_Width()
        {
            /* Width is fixed for the buffer's lifetime. */
            return(FB_iWidth);
        }

        /*---------------------------------------------------------------------*/
        /* FB_Height:                                                          */
        /*                                                                     */
        /* The image height in pixels, fixed for the buffer's lifetime.  The   */
        /* server reads it for ServerInit and update rectangles, and a painter */
        /* reads it to size its writes.                                        */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     None.                                                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     int : the height in pixels.                                     */
        /*---------------------------------------------------------------------*/

        public int FB_Height()
        {
            /* Height is fixed for the buffer's lifetime. */
            return(FB_iHeight);
        }

        /*--------------------------------------------------------------------*/
        /* FB_TakeDirty:                                                      */
        /*                                                                    */
        /* Atomically reads and clears the dirty flag, so a server sends a    */
        /* frame exactly when there is a change to send.                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true if the buffer changed since the last call.         */
        /*--------------------------------------------------------------------*/

        public bool FB_TakeDirty()
        {
            bool bWas;                           // dirty state before clearing

            lock (FB_oLock)
            {   /* Read-and-clear under the lock so no change is missed. */
                bWas      = FB_bDirty;
                FB_bDirty = false;
            }

            /* The dirty state seen before this call cleared it. */
            return(bWas);
        }

        /*--------------------------------------------------------------------*/
        /* FB_Buffer:                                                         */
        /*                                                                    */
        /* Returns the raw pixel array, {R,G,B,pad} per pixel, row-major, for */
        /* a caller to paint into.  The array reference is fixed for the      */
        /* buffer's lifetime; hold FB_SyncRoot while writing so an encode     */
        /* cannot read a half-painted frame, and call FB_MarkDirty after.     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     byte[] : the width*height*4 pixel buffer, written in place.    */
        /*--------------------------------------------------------------------*/

        public byte[] FB_Buffer()
        {
            /* The backing store; stable for the buffer's lifetime. */
            return(FB_abPixels);
        }

        /*--------------------------------------------------------------------*/
        /* FB_SyncRoot:                                                       */
        /*                                                                    */
        /* Returns the lock that guards the pixels and the dirty flag.  A     */
        /* caller painting a whole frame locks on this so its write and the   */
        /* server's encode are mutually exclusive - the same guarantee the    */
        /* built-in encoder relies on.                                        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     object : the monitor object to lock during a paint.            */
        /*--------------------------------------------------------------------*/

        public object FB_SyncRoot()
        {
            /* One lock serialises paints against encodes. */
            return(FB_oLock);
        }

        /*--------------------------------------------------------------------*/
        /* FB_MarkDirty:                                                      */
        /*                                                                    */
        /* Marks the buffer changed since the last send, so the next update   */
        /* request is answered with a frame.  The lock is re-entrant, so a    */
        /* caller may call this while already holding FB_SyncRoot.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : sets the dirty flag under the lock.                     */
        /*--------------------------------------------------------------------*/

        public void FB_MarkDirty()
        {
            lock (FB_oLock)
            {   /* Flag the change; a concurrent FB_TakeDirty sees it next. */
                FB_bDirty = true;
            }
        }

        /*---------------------------------------------------------------------*/
        /* FB_EncodeRawRect:                                                   */
        /*                                                                     */
        /* Encodes a rectangle of the framebuffer into RAW pixel bytes in the  */
        /* client's negotiated format, row-major, ready to append after an RFB */
        /* rectangle header.  The caller passes a rectangle within the image   */
        /* bounds.                                                             */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     iX   : rectangle left.                                          */
        /*     iY   : rectangle top.                                           */
        /*     iW   : rectangle width.                                         */
        /*     iH   : rectangle height.                                        */
        /*     fmt  : the client pixel format to encode into.                  */
        /*                                                                     */
        /* Returns:                                                            */
        /*     byte[] : iW*iH*bytesPerPixel encoded bytes.                     */
        /*---------------------------------------------------------------------*/

        public byte[] FB_EncodeRawRect(int iX, int iY, int iW, int iH, clsPixelFormat fmt)
        {
            byte[] abOut;                        // encoded RAW bytes
            int    iBpp;                         // destination bytes per pixel
            int    iDst;                         // running output offset
            int    iSrc;                         // source pixel offset
            int    x;
            int    y;

            iBpp = fmt.PIXF_iBytesPerPixel;

            lock (FB_oLock)
            {   /* Encode under the lock so a concurrent paint cannot tear a row. */
                abOut = new byte[iW * iH * iBpp];
                iDst  = 0;

                for (y = iY; y < (iY + iH); y++)
                {   /* One source row per output row. */
                    for (x = iX; x < (iX + iW); x++)
                    {   /* Read the stored RGB and pack it into the client format. */
                        iSrc = ((y * FB_iWidth) + x) * 4;

                        fmt.PIXF_EncodePixel(FB_abPixels[iSrc + 0],
                                             FB_abPixels[iSrc + 1],
                                             FB_abPixels[iSrc + 2],
                                             abOut, iDst);
                        iDst += iBpp;
                    }
                }
            }

            /* The encoded rectangle, in client pixel order. */
            return(abOut);
        }
    }
}
