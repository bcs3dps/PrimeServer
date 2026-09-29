/*-------------------------------------------------------------------------------*/
/* SPDX-License-Identifier: GPL-2.0-or-later                                     */
/* Copyright (c) 2026 B. C. Services                                             */
/*                                                                               */
/* Licensed under the GNU General Public License version 2 or later --           */
/* see LICENSE beside this source.  GPL-2.0 full text in GPL-2.0.txt.            */
/*-------------------------------------------------------------------------------*/
/* RfbServer.cs:                                                                 */
/*                                                                               */
/* A minimal RFB (VNC) SERVER, client-facing side of RFC 6143, serving a         */
/* synthetic clsFrameBuffer.  It exercises a VNC client end to end with no       */
/* display server and no real hardware: version and security handshake,          */
/* VNC-Authentication (challenge/response verified with the in-house DES) or     */
/* None, ServerInit, then a message loop that answers FramebufferUpdateRequests  */
/* with RAW rectangles in the client's negotiated format and hands PointerEvents */
/* and the content schedule to callbacks the consumer registers (SRV_SetHooks).  */
/*                                                                               */
/* One thread per client; a shared framebuffer.  Pure C# 5.0, mscorlib + System  */
/* only, so it can be reused by any program needing a headless RFB server over   */
/* an in-memory framebuffer.                                                     */
/*-------------------------------------------------------------------------------*/

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using Des;
using PixelFormat;
using FrameBuffer;

namespace RfbServer
{
    /*------------------------------------------------------------------------*/
    /* clsRfbServer:                                                          */
    /*                                                                        */
    /* Owns the listener, the password and the shared framebuffer, and drives */
    /* each client connection through the RFB protocol.                       */
    /*------------------------------------------------------------------------*/

    public sealed class clsRfbServer
    {
        private const int SRV_HANDSHAKE_TIMEOUT_MS = 15000;  // read timeout during setup

        /* RFB message type codes (RFC 6143 sections 7.4, 7.5). */
        private const int SRV_CMSG_SETPIXFMT    = 0;   // client -> server SetPixelFormat
        private const int SRV_CMSG_SETENCODINGS = 2;   // client -> server SetEncodings
        private const int SRV_CMSG_UPDATEREQ    = 3;   // client -> server FramebufferUpdateRequest
        private const int SRV_CMSG_KEYEVENT     = 4;   // client -> server KeyEvent
        private const int SRV_CMSG_POINTER      = 5;   // client -> server PointerEvent
        private const int SRV_CMSG_CUTTEXT      = 6;   // client -> server ClientCutText

        private const int SRV_SEC_NONE          = 1;   // security type None
        private const int SRV_SEC_VNCAUTH       = 2;   // security type VNC Authentication

        private readonly int           SRV_iPort;      // listen port
        private readonly string        SRV_sPassword;  // VNC password ("" = None security)
        private readonly clsFrameBuffer SRV_fb;        // shared synthetic framebuffer
        private readonly bool          SRV_bVerbose;   // diagnostic logging

        private Func<int>              SRV_fnNextEventMs;  // ms until the content's next scheduled change, or -1
        private Action                 SRV_fnTick;         // do the content's due scheduled work
        private Func<string>           SRV_fnDescribe;     // one-phrase content description for the banner
        private Action<int, int, int>  SRV_fnPointer;      // handle a PointerEvent (x, y, button-mask)

        /*---------------------------------------------------------------------*/
        /* clsRfbServer (constructor):                                         */
        /*                                                                     */
        /* Records the listen port, password, framebuffer and verbosity, and   */
        /* installs do-nothing content hooks (SRV_SetHooks registers a real    */
        /* content driver: what is shown, how it moves, and pointer handling). */
        /* No socket is opened until SRV_Run.                                  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     iPort     : TCP port to listen on (RFB is 5900 + display).      */
        /*     sPassword : VNC password; empty string selects None security.   */
        /*     fb        : the shared synthetic framebuffer to serve.          */
        /*     bVerbose  : true for per-message diagnostic logging.            */
        /*                                                                     */
        /* Returns:                                                            */
        /*     clsRfbServer : a configured, not-yet-listening server.          */
        /*---------------------------------------------------------------------*/

        public clsRfbServer(int iPort, string sPassword, clsFrameBuffer fb, bool bVerbose)
        {
            SRV_iPort       = iPort;
            SRV_sPassword   = sPassword;
            SRV_fb          = fb;
            SRV_bVerbose    = bVerbose;

            /* Default content hooks do nothing: a bare server serves the        */
            /* framebuffer, waits for update requests, and ignores pointers.     */
            /* SRV_SetHooks replaces them with a content driver (the harness).   */
            SRV_fnNextEventMs = SRV_DefaultNextEventMs;
            SRV_fnTick        = SRV_DefaultTick;
            SRV_fnDescribe    = SRV_DefaultDescribe;
            SRV_fnPointer     = SRV_DefaultPointer;
        }

        /*----------------------------------------------------------------------*/
        /* SRV_SetHooks:                                                        */
        /*                                                                      */
        /* Registers the content driver's callbacks, replacing the do-nothing   */
        /* defaults: how long until the content next changes on a schedule, the */
        /* action that performs a due change, a one-phrase banner description,  */
        /* and a PointerEvent handler.  A consumer that only marks the          */
        /* framebuffer dirty when it renders leaves these unset.                */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     fnNextEventMs : returns ms to the next scheduled change, or -1.  */
        /*     fnTick        : performs whatever change has fallen due.         */
        /*     fnDescribe    : returns a banner phrase (empty for none).        */
        /*     fnPointer     : handles a PointerEvent (x, y, button-mask).      */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : records the four callbacks.                               */
        /*----------------------------------------------------------------------*/

        public void SRV_SetHooks(Func<int> fnNextEventMs, Action fnTick,
                                 Func<string> fnDescribe, Action<int, int, int> fnPointer)
        {
            SRV_fnNextEventMs = fnNextEventMs;
            SRV_fnTick        = fnTick;
            SRV_fnDescribe    = fnDescribe;
            SRV_fnPointer     = fnPointer;
        }

        /*----------------------------------------------------------------------*/
        /* SRV_DefaultNextEventMs:                                              */
        /*                                                                      */
        /* The default next-event hook: no content change is ever scheduled, so */
        /* the server may wait for a client message for ever.  SRV_SetHooks     */
        /* replaces it when a consumer drives content on a schedule.  Local.    */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     None.                                                            */
        /*                                                                      */
        /* Returns:                                                             */
        /*     int : always -1 (no scheduled event).                            */
        /*----------------------------------------------------------------------*/

        private static int SRV_DefaultNextEventMs()
        {
            /* No schedule: the server may wait for a client message for ever. */
            return(-1);
        }

        /*----------------------------------------------------------------------*/
        /* SRV_DefaultTick:                                                     */
        /*                                                                      */
        /* The default tick hook: nothing is scheduled, so it does nothing.     */
        /* SRV_SetHooks replaces it when a consumer has work due at a deadline. */
        /* Local.                                                               */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     None.                                                            */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : does nothing.                                             */
        /*----------------------------------------------------------------------*/

        private static void SRV_DefaultTick()
        {
            /* Nothing scheduled to do. */
        }

        /*--------------------------------------------------------------------*/
        /* SRV_DefaultDescribe:                                               */
        /*                                                                    */
        /* The default describe hook: no phrase to add to the startup banner. */
        /* SRV_SetHooks replaces it when a consumer wants to name what it is  */
        /* serving.  Local.                                                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     None.                                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the empty string (nothing to add).                    */
        /*--------------------------------------------------------------------*/

        private static string SRV_DefaultDescribe()
        {
            /* No content description to add to the banner. */
            return("");
        }

        /*--------------------------------------------------------------------*/
        /* SRV_DefaultPointer:                                                */
        /*                                                                    */
        /* The default pointer hook: a bare server reflects pointer events    */
        /* nowhere, so it ignores them.  SRV_SetHooks replaces it when a      */
        /* consumer handles input.  Local.                                    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     iX    : pointer x (ignored).                                   */
        /*     iY    : pointer y (ignored).                                   */
        /*     iMask : button mask (ignored).                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : does nothing.                                           */
        /*--------------------------------------------------------------------*/

        private static void SRV_DefaultPointer(int iX, int iY, int iMask)
        {
            /* A bare server does not reflect pointer events anywhere. */
        }

        /*--------------------------------------------------------------------*/
        /* SRV_Log:                                                           */
        /*                                                                    */
        /* Prints a diagnostic line to stderr when verbose.  Local helper so  */
        /* the protocol code stays readable.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sMsg : the message (already formatted by the caller).          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : writes to stderr only when verbose.                     */
        /*--------------------------------------------------------------------*/

        private void SRV_Log(string sMsg)
        {
            if (SRV_bVerbose)
            {   /* Diagnostics go to stderr so stdout stays clean. */
                Console.Error.WriteLine("primeserve: " + sMsg);
            }
        }

        /*---------------------------------------------------------------------*/
        /* SRV_Run:                                                            */
        /*                                                                     */
        /* Binds the listener and accepts clients.  With bOnce it serves a     */
        /* single client on the calling thread and returns (the test mode);    */
        /* otherwise it loops forever, handling each client on its own thread. */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     bOnce : true to serve exactly one client and return.            */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : returns after one client (bOnce) or runs until killed.   */
        /*---------------------------------------------------------------------*/

        public void SRV_Run(bool bOnce)
        {
            TcpListener listener;                // the accept socket
            string      sMode;                   // banner suffix describing the content mode
            string      sDesc;                   // the content driver's description phrase

            /* Listen on all interfaces so a client on the host or in WSL can reach it. */
            listener = new TcpListener(IPAddress.Any, SRV_iPort);
            listener.Start();

            /* Name what the client will be shown - the content driver's phrase,   */
            /* if any - so a slow link or a surprising picture can be reasoned     */
            /* about from the log alone.                                           */
            sDesc = SRV_fnDescribe();
            sMode = (sDesc.Length > 0) ? (", " + sDesc) : "";

            Console.Error.WriteLine("primeserve: listening on port " + SRV_iPort +
                                    " (" + SRV_fb.FB_Width() + "x" + SRV_fb.FB_Height() + sMode +
                                    (SRV_sPassword.Length > 0 ? ", VNC auth)" : ", no auth)"));

            if (bOnce)
            {   /* Test mode: one client, synchronously, then done. */
                TcpClient client = listener.AcceptTcpClient();
                SRV_HandleClient(client);
                listener.Stop();
                return;
            }

            for (;;)
            {   /* Serve indefinitely, one thread per client. */
                TcpClient client = listener.AcceptTcpClient();
                Thread    thread = new Thread(SRV_ClientThread);
                thread.IsBackground = true;
                thread.Start(client);
            }
        }

        /*--------------------------------------------------------------------*/
        /* SRV_ClientThread:                                                  */
        /*                                                                    */
        /* Thread entry that adapts the object parameter and delegates to     */
        /* SRV_HandleClient.  Local.                                          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     oClient : the TcpClient, boxed as object by the Thread API.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : handles the one client and exits the thread.            */
        /*--------------------------------------------------------------------*/

        private void SRV_ClientThread(object oClient)
        {
            /* The Thread start signature hands us the client as object. */
            SRV_HandleClient((TcpClient)oClient);
        }

        /*---------------------------------------------------------------------*/
        /* SRV_HandleClient:                                                   */
        /*                                                                     */
        /* Runs the whole RFB conversation for one client, catching any I/O or */
        /* protocol error so a bad client cannot take the server down.         */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     client : the accepted connection.                               */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : serves the client until it disconnects or errors.        */
        /*---------------------------------------------------------------------*/

        private void SRV_HandleClient(TcpClient client)
        {
            NetworkStream stream;                // the client's byte stream

            SRV_Log("client connected from " + client.Client.RemoteEndPoint);

            try
            {   /* The whole conversation; the catch below turns any I/O or   */
                /* protocol error into a clean drop of THIS client, never a   */
                /* server stop, so one bad client cannot fell the listener.   */
                client.NoDelay = true;
                stream = client.GetStream();

                /* Handshake and authentication run under a read timeout so a   */
                /* stalled client cannot hang a server thread forever.          */
                stream.ReadTimeout = SRV_HANDSHAKE_TIMEOUT_MS;

                if (SRV_Negotiate(stream))
                {   /* Authenticated and initialised: run the message loop,   */
                    /* which sets its own read timeout as it goes.            */
                    SRV_MessageLoop(stream);
                }
            }
            catch (Exception ex)
            {   /* Any failure ends this client cleanly; log it when verbose. */
                SRV_Log("client ended: " + ex.Message);
            }
            finally
            {   /* Always release the socket. */
                client.Close();
            }
        }

        /*---------------------------------------------------------------------*/
        /* SRV_ReadFull:                                                       */
        /*                                                                     */
        /* Reads exactly iLen bytes into abBuf at iOff, looping over short     */
        /* reads.  Returns false on an orderly close (Read returns 0).  Local. */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     stream : the client stream.                                     */
        /*     abBuf  : destination buffer.                                    */
        /*     iOff   : offset to write at.                                    */
        /*     iLen   : number of bytes to read.                               */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool : true if all bytes were read, false if the peer closed.   */
        /*---------------------------------------------------------------------*/

        private static bool SRV_ReadFull(NetworkStream stream, byte[] abBuf, int iOff, int iLen)
        {
            int iGot;                            // bytes from one Read
            int iDone;                           // bytes read so far

            iDone = 0;

            while (iDone < iLen)
            {   /* Each pass fills as much of the remainder as one Read yields. */
                iGot = stream.Read(abBuf, iOff + iDone, iLen - iDone);

                if (iGot <= 0)
                {   /* Peer closed the connection. */
                    return(false);
                }

                iDone += iGot;
            }

            /* Every requested byte arrived. */
            return(true);
        }

        /*----------------------------------------------------------------------*/
        /* SRV_Discard:                                                         */
        /*                                                                      */
        /* Reads and throws away lLen bytes (a variable-length client field).   */
        /* The count is a long, not an int, on purpose: a field length carried  */
        /* on the wire as an unsigned 32-bit value - a ClientCutText length can */
        /* be up to 4 GiB - must be consumed in full.  Casting it to int would  */
        /* turn any value above int.MaxValue negative, the loop would exit at   */
        /* once having read nothing, and the unread field bytes would then be   */
        /* misread as the next message type, desyncing the stream.  Local.      */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     stream : the client stream.                                      */
        /*     lLen   : bytes to consume (>= 0; 0 consumes nothing).            */
        /*                                                                      */
        /* Returns:                                                             */
        /*     bool : true if consumed, false on a close.                       */
        /*----------------------------------------------------------------------*/

        private static bool SRV_Discard(NetworkStream stream, long lLen)
        {
            byte[] abScratch;                    // landing buffer
            int    iChunk;                       // bytes this pass (always <= 512)
            long   lRemaining;                   // bytes left, wide enough for a u32 field

            abScratch  = new byte[512];
            lRemaining = lLen;

            while (lRemaining > 0)
            {   /* Consume up to a scratch buffer at a time. */
                iChunk = (lRemaining > abScratch.Length) ? abScratch.Length : (int)lRemaining;

                if (!SRV_ReadFull(stream, abScratch, 0, iChunk))
                {   /* Peer closed mid-field. */
                    return(false);
                }

                lRemaining -= iChunk;
            }

            /* All bytes consumed. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* SRV_PutU16:                                                        */
        /*                                                                    */
        /* Stores a 16-bit value big-endian (RFB wire order: high byte first) */
        /* at abBuf[iOff].  Local.                                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abBuf : destination buffer (at least iOff + 2 bytes).          */
        /*     iOff  : offset to write at.                                    */
        /*     iVal  : value to store; only the low 16 bits are written.      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : writes 2 bytes.                                         */
        /*--------------------------------------------------------------------*/

        private static void SRV_PutU16(byte[] abBuf, int iOff, int iVal)
        {
            /* High byte first. */
            abBuf[iOff + 0] = (byte)((iVal >> 8) & 0xFF);
            abBuf[iOff + 1] = (byte)(iVal & 0xFF);
        }

        /*----------------------------------------------------------------------*/
        /* SRV_PutU32:                                                          */
        /*                                                                      */
        /* Stores a 32-bit value big-endian (RFB wire order) at abBuf[iOff].    */
        /* The value is taken as a long so an unsigned 32-bit quantity - a      */
        /* length or a pixel count - passes through without its top bit turning */
        /* it negative on the way in.  Local.                                   */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     abBuf : destination buffer (at least iOff + 4 bytes).            */
        /*     iOff  : offset to write at.                                      */
        /*     lVal  : value to store; only the low 32 bits are written.        */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : writes 4 bytes.                                           */
        /*----------------------------------------------------------------------*/

        private static void SRV_PutU32(byte[] abBuf, int iOff, long lVal)
        {
            /* Most significant byte first. */
            abBuf[iOff + 0] = (byte)((lVal >> 24) & 0xFF);
            abBuf[iOff + 1] = (byte)((lVal >> 16) & 0xFF);
            abBuf[iOff + 2] = (byte)((lVal >> 8) & 0xFF);
            abBuf[iOff + 3] = (byte)(lVal & 0xFF);
        }

        /*--------------------------------------------------------------------*/
        /* SRV_GetU16:                                                        */
        /*                                                                    */
        /* Reads a big-endian 16-bit value from a buffer.  Local.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abBuf : source buffer.                                         */
        /*     iOff  : offset to read from.                                   */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : the decoded value.                                       */
        /*--------------------------------------------------------------------*/

        private static int SRV_GetU16(byte[] abBuf, int iOff)
        {
            /* High byte then low byte. */
            return(((abBuf[iOff + 0] & 0xFF) << 8) | (abBuf[iOff + 1] & 0xFF));
        }

        /*----------------------------------------------------------------------*/
        /* SRV_Negotiate:                                                       */
        /*                                                                      */
        /* Version exchange, security selection, authentication, ClientInit and */
        /* ServerInit.  Leaves the connection ready for the message loop.       */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     stream : the client stream.                                      */
        /*                                                                      */
        /* Returns:                                                             */
        /*     bool : true when authenticated and initialised, else false.      */
        /*----------------------------------------------------------------------*/

        private bool SRV_Negotiate(NetworkStream stream)
        {
            byte[] abVer;                        // 12-byte version banners
            byte[] abOne;                        // one-byte fields
            int    iClientMinor;                 // client's minor version
            int    iSecType;                     // security type in use

            /* Send our ProtocolVersion (3.8). */
            abVer = System.Text.Encoding.ASCII.GetBytes("RFB 003.008\n");
            stream.Write(abVer, 0, 12);

            /* Read the client's chosen ProtocolVersion. */
            abVer = new byte[12];
            if (!SRV_ReadFull(stream, abVer, 0, 12))
            {   /* No version from the client. */
                return(false);
            }

            iClientMinor = ((abVer[8] - '0') * 100) + ((abVer[9] - '0') * 10) + (abVer[10] - '0');
            SRV_Log("client version 3." + iClientMinor);

            /* Choose the security type: VNC auth if we have a password, else None. */
            iSecType = (SRV_sPassword.Length > 0) ? SRV_SEC_VNCAUTH : SRV_SEC_NONE;

            if (iClientMinor >= 7)
            {   /* 3.7 / 3.8: offer a one-entry list; the client echoes its choice. */
                abOne = new byte[2];
                abOne[0] = 1;                    // one security type on offer
                abOne[1] = (byte)iSecType;
                stream.Write(abOne, 0, 2);

                abOne = new byte[1];
                if (!SRV_ReadFull(stream, abOne, 0, 1))
                {   /* Client did not send its choice. */
                    return(false);
                }

                if (abOne[0] != (byte)iSecType)
                {   /* A 3.7+ client must echo one of the types we offered;   */
                    /* we offered exactly one, so any other value is a        */
                    /* broken or hostile client and the connection drops.     */
                    SRV_Log("client selected unexpected security type " + abOne[0]);
                    return(false);
                }
            }
            else
            {   /* 3.3: the server dictates the type as a single u32; no echo. */
                abOne = new byte[4];
                SRV_PutU32(abOne, 0, iSecType);
                stream.Write(abOne, 0, 4);
            }

            /* Authentication and SecurityResult. */
            if (!SRV_Authenticate(stream, iClientMinor, iSecType))
            {   /* Auth failed or the client dropped. */
                return(false);
            }

            /* ClientInit: one shared-flag byte (value ignored by this server). */
            abOne = new byte[1];
            if (!SRV_ReadFull(stream, abOne, 0, 1))
            {   /* No ClientInit. */
                return(false);
            }

            /* ServerInit. */
            SRV_SendServerInit(stream);

            /* Version, security, authentication and ClientInit all succeeded:     */
            /* the connection is ready for SRV_HandleClient to enter the message   */
            /* loop.                                                               */
            return(true);
        }

        /*-----------------------------------------------------------------------*/
        /* SRV_Authenticate:                                                     */
        /*                                                                       */
        /* Runs the chosen security type's exchange and sends the SecurityResult */
        /* where the version/type require it.                                    */
        /*                                                                       */
        /* Arguments:                                                            */
        /*     stream   : the client stream.                                     */
        /*     iMinor   : client minor version.                                  */
        /*     iSecType : the security type in use.                              */
        /*                                                                       */
        /* Returns:                                                              */
        /*     bool : true if authentication succeeded.                          */
        /*-----------------------------------------------------------------------*/

        private bool SRV_Authenticate(NetworkStream stream, int iMinor, int iSecType)
        {
            byte[] abChallenge;                  // 16-byte challenge we send
            byte[] abResponse;                   // 16-byte response from client
            byte[] abExpected;                   // response we expect
            byte[] abResult;                     // 4-byte SecurityResult
            bool   bOk;                          // response matched
            bool   bSendResult;                  // whether a SecurityResult is due
            int    i;

            if (iSecType == SRV_SEC_VNCAUTH)
            {   /* VNC auth: challenge/response verified with the shared DES. */
                abChallenge = new byte[16];

                using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
                {   /* A fresh random challenge per connection. */
                    rng.GetBytes(abChallenge);
                }

                stream.Write(abChallenge, 0, 16);

                abResponse = new byte[16];
                if (!SRV_ReadFull(stream, abResponse, 0, 16))
                {   /* No response. */
                    return(false);
                }

                /* Compare against the response we compute for our password. */
                abExpected = clsDes.DES_VncEncrypt(SRV_sPassword, abChallenge);

                /* Compare all 16 bytes with no early-out, so a wrong     */
                /* password takes as long to reject as a right one does   */
                /* to accept - the reply cannot be timed to find the      */
                /* password faster.  This is not a hardened constant-     */
                /* time compare (the mismatch branch is data-dependent)   */
                /* but it removes the obvious early-exit signal, which    */
                /* is enough on a LAN-trusted link.                       */
                bOk = true;
                for (i = 0; i < 16; i++)
                {   /* One response byte per iteration; never break early. */
                    if (abResponse[i] != abExpected[i])
                    {   /* A single mismatch is a wrong password, but keep going. */
                        bOk = false;
                    }
                }

                bSendResult = true;              // VNC auth always gets a SecurityResult
            }
            else
            {   /* None: nothing to exchange; 3.8 still sends a SecurityResult. */
                bOk         = true;
                bSendResult = (iMinor >= 8);
            }

            if (bSendResult)
            {   /* 0 = OK, 1 = failed. */
                abResult = new byte[4];
                SRV_PutU32(abResult, 0, bOk ? 0 : 1);
                stream.Write(abResult, 0, 4);

                if (!bOk && (iMinor >= 8))
                {   /* 3.8 appends a reason string after a failure. */
                    byte[] abReason = System.Text.Encoding.ASCII.GetBytes("Authentication failed");
                    byte[] abLen    = new byte[4];
                    SRV_PutU32(abLen, 0, abReason.Length);
                    stream.Write(abLen, 0, 4);
                    stream.Write(abReason, 0, abReason.Length);
                }
            }

            if (!bOk)
            {   /* Report and reject. */
                SRV_Log("authentication failed");
                return(false);
            }

            SRV_Log("authenticated");

            /* Authenticated, or None accepted: the caller proceeds to ClientInit. */
            return(true);
        }

        /*--------------------------------------------------------------------*/
        /* SRV_SendServerInit:                                                */
        /*                                                                    */
        /* Sends ServerInit: framebuffer size, the server's advertised pixel  */
        /* format, and the desktop name.                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stream : the client stream.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : writes ServerInit.                                      */
        /*--------------------------------------------------------------------*/

        private void SRV_SendServerInit(NetworkStream stream)
        {
            byte[]        abName;       // desktop name bytes
            byte[]        abInit;       // the assembled ServerInit
            clsPixelFormat fmtDefault;  // advertised format

            abName     = System.Text.Encoding.ASCII.GetBytes("PrimeServe");
            fmtDefault = clsPixelFormat.PIXF_CreateDefault();

            /* width(2) + height(2) + pixel-format(16) + name-length(4) + name. */
            abInit = new byte[24 + abName.Length];
            SRV_PutU16(abInit, 0, SRV_fb.FB_Width());
            SRV_PutU16(abInit, 2, SRV_fb.FB_Height());
            fmtDefault.PIXF_WriteWire(abInit, 4);
            SRV_PutU32(abInit, 20, abName.Length);
            Array.Copy(abName, 0, abInit, 24, abName.Length);

            stream.Write(abInit, 0, abInit.Length);
        }

        /*---------------------------------------------------------------------*/
        /* SRV_IsReadTimeout:                                                  */
        /*                                                                     */
        /* Tells a NetworkStream read that only hit its ReadTimeout apart from */
        /* a real I/O failure: the stream wraps the socket's TimedOut error in */
        /* an IOException, so the inner exception is what says which.  Local.  */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     ex : the exception the read threw.                              */
        /*                                                                     */
        /* Returns:                                                            */
        /*     bool : true for an expired ReadTimeout, false otherwise.        */
        /*---------------------------------------------------------------------*/

        private static bool SRV_IsReadTimeout(IOException ex)
        {
            SocketException sockEx;              // the socket error behind the stream error

            sockEx = ex.InnerException as SocketException;

            if (sockEx == null)
            {   /* Not a socket error at all (e.g. the stream was disposed). */
                return(false);
            }

            /* NetworkStream reports an expired ReadTimeout as a TimedOut socket error. */
            return(sockEx.SocketErrorCode == SocketError.TimedOut);
        }

        /*--------------------------------------------------------------------*/
        /* SRV_MessageLoop:                                                   */
        /*                                                                    */
        /* Reads and answers client messages until the client disconnects:    */
        /* pixel format, encodings, update requests (answered with a full RAW */
        /* frame when due), pointer events (reflected as a marker), and the   */
        /* messages consumed and ignored.  The wait for the next message is   */
        /* bounded by the framebuffer's next scheduled event (a gradient step */
        /* or a screen switch) and each timeout ticks the schedule, any new   */
        /* frame going at once to a client holding an incremental request -   */
        /* one control loop, no timer thread.                                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     stream : the client stream.                                    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : returns when the client disconnects.                    */
        /*--------------------------------------------------------------------*/

        private void SRV_MessageLoop(NetworkStream stream)
        {
            int           iWait;       // ms until the framebuffer's next event; -1 = none
            clsPixelFormat fmtClient;  // format the client asked pixels in
            byte[]        abType;      // one message-type byte
            byte[]        abBody;      // per-message fixed body
            bool          bPending;    // a non-dirty incremental is waiting
            bool          bGotType;    // a message-type byte arrived this pass

            /* Until SetPixelFormat arrives, assume the advertised default. */
            fmtClient = clsPixelFormat.PIXF_CreateDefault();
            bPending  = false;

            abType = new byte[1];

            for (;;)
            {   /* One message per pass, or one schedule tick when nothing arrives. */

                /* Wait at most until the content's next scheduled event and    */
                /* treat a timeout as the tick.  With nothing scheduled, wait   */
                /* for ever, since updates are request-driven and may idle.     */
                iWait = SRV_fnNextEventMs();

                if (iWait > 0)
                {   /* Something is scheduled: the read doubles as its timer. */
                    stream.ReadTimeout = iWait;
                }
                else
                {   /* Nothing scheduled: no timeout at all. */
                    stream.ReadTimeout = Timeout.Infinite;
                }

                try
                {   /* Read one message-type byte.  This read carries the    */
                    /* schedule timeout, so a ReadTimeout here is the tick   */
                    /* falling due, not a failure - the catch tells them     */
                    /* apart.                                                */
                    bGotType = SRV_ReadFull(stream, abType, 0, 1);
                }
                catch (IOException ex)
                {   /* Only an expired read timeout is a tick; anything else ends the client. */
                    if (!SRV_IsReadTimeout(ex))
                    {   /* Real failure: let SRV_HandleClient log it and close. */
                        throw;
                    }

                    /* Tick: let the content driver do what is due (or         */
                    /* nothing if the timer ran a little early).  A client     */
                    /* already holding an incremental request gets the fresh   */
                    /* frame at once; one that has not asked gets it on its    */
                    /* next request (RFB is pull).                             */
                    SRV_fnTick();

                    if (bPending && SRV_fb.FB_TakeDirty())
                    {   /* Held request satisfied by a fresh frame. */
                        SRV_SendFullUpdate(stream, fmtClient);
                        bPending = false;
                    }

                    continue;
                }

                if (!bGotType)
                {   /* Client closed. */
                    SRV_Log("client disconnected");
                    return;
                }

                /* The rest of the message is read with no timeout, so a   */
                /* body arriving in pieces is never mistaken for a tick.   */
                stream.ReadTimeout = Timeout.Infinite;

                if (abType[0] == SRV_CMSG_SETPIXFMT)
                {   /* SetPixelFormat: 3 padding + 16-byte format. */
                    abBody = new byte[19];
                    if (!SRV_ReadFull(stream, abBody, 0, 19))
                    {   /* Truncated. */
                        return;
                    }

                    fmtClient = clsPixelFormat.PIXF_ReadWire(abBody, 3);
                    SRV_Log("SetPixelFormat: " + (fmtClient.PIXF_iBytesPerPixel * 8) + " bpp");
                }
                else if (abType[0] == SRV_CMSG_SETENCODINGS)
                {   /* SetEncodings: 1 padding + count(2) + count*4; contents ignored. */
                    abBody = new byte[3];
                    if (!SRV_ReadFull(stream, abBody, 0, 3))
                    {   /* Truncated. */
                        return;
                    }

                    int iCount = SRV_GetU16(abBody, 1);
                    if (!SRV_Discard(stream, iCount * 4))
                    {   /* Truncated encoding list. */
                        return;
                    }
                }
                else if (abType[0] == SRV_CMSG_UPDATEREQ)
                {   /* FramebufferUpdateRequest: incremental(1) + x,y,w,h(8). */
                    abBody = new byte[9];
                    if (!SRV_ReadFull(stream, abBody, 0, 9))
                    {   /* Truncated. */
                        return;
                    }

                    /* The requested rectangle (abBody[1..8]) is read but      */
                    /* not honoured: the server always sends the whole         */
                    /* framebuffer, which RFC 6143 permits (an update may      */
                    /* cover more than was asked), keeping the encoder to      */
                    /* one full-frame path.  Only the incremental flag acts.   */
                    bool bIncremental = (abBody[0] != 0);

                    if (!bIncremental)
                    {   /* Non-incremental: always send the whole frame now. */
                        SRV_fb.FB_TakeDirty();   // consume any pending change flag
                        SRV_SendFullUpdate(stream, fmtClient);
                        bPending = false;
                    }
                    else if (SRV_fb.FB_TakeDirty())
                    {   /* Incremental and something changed: send it. */
                        SRV_SendFullUpdate(stream, fmtClient);
                        bPending = false;
                    }
                    else
                    {   /* Incremental with no change: hold until the buffer dirties. */
                        bPending = true;
                    }
                }
                else if (abType[0] == SRV_CMSG_POINTER)
                {   /* PointerEvent: button-mask(1) + x(2) + y(2). */
                    abBody = new byte[5];
                    if (!SRV_ReadFull(stream, abBody, 0, 5))
                    {   /* Truncated. */
                        return;
                    }

                    int iMask = abBody[0] & 0xFF;
                    int iX    = SRV_GetU16(abBody, 1);
                    int iY    = SRV_GetU16(abBody, 3);
                    SRV_Log("PointerEvent mask=" + iMask + " x=" + iX + " y=" + iY);

                    /* Hand the pointer event to the registered handler      */
                    /* (the harness reflects it as a marker), then satisfy   */
                    /* a held incremental request.                           */
                    SRV_fnPointer(iX, iY, iMask);

                    if (bPending)
                    {   /* A client was waiting for a change; send it now. */
                        SRV_fb.FB_TakeDirty();
                        SRV_SendFullUpdate(stream, fmtClient);
                        bPending = false;
                    }
                }
                else if (abType[0] == SRV_CMSG_KEYEVENT)
                {   /* KeyEvent: down-flag(1) + padding(2) + key(4); ignored. */
                    abBody = new byte[7];
                    if (!SRV_ReadFull(stream, abBody, 0, 7))
                    {   /* Truncated. */
                        return;
                    }
                }
                else if (abType[0] == SRV_CMSG_CUTTEXT)
                {   /* ClientCutText: padding(3) + length(4) + text; ignored. */
                    abBody = new byte[7];
                    if (!SRV_ReadFull(stream, abBody, 0, 7))
                    {   /* Truncated. */
                        return;
                    }

                    /* Assemble the big-endian u32 length via unsigned        */
                    /* casts so no operand is sign-extended into the OR       */
                    /* (CS0675).  It is passed to SRV_Discard as a long,      */
                    /* un-truncated, so even a length above int.MaxValue is   */
                    /* fully consumed instead of desyncing the stream.        */
                    long lLen = ((uint)abBody[3] << 24) | ((uint)abBody[4] << 16) |
                                ((uint)abBody[5] << 8)  | (uint)abBody[6];
                    if (!SRV_Discard(stream, lLen))
                    {   /* Truncated cut text. */
                        return;
                    }
                }
                else
                {   /* Unknown message type: the stream is out of sync. */
                    SRV_Log("unknown client message type " + abType[0]);
                    return;
                }
            }
        }

        /*----------------------------------------------------------------------*/
        /* SRV_SendFullUpdate:                                                  */
        /*                                                                      */
        /* Sends a FramebufferUpdate carrying the entire framebuffer as one RAW */
        /* rectangle in the client's pixel format.                              */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     stream    : the client stream.                                   */
        /*     fmtClient : the pixel format to encode into.                     */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : writes one FramebufferUpdate.                             */
        /*----------------------------------------------------------------------*/

        private void SRV_SendFullUpdate(NetworkStream stream, clsPixelFormat fmtClient)
        {
            byte[] abHdr;                        // update + rectangle headers
            byte[] abPixels;                     // encoded RAW pixels
            int    iW;
            int    iH;

            iW = SRV_fb.FB_Width();
            iH = SRV_fb.FB_Height();

            /* message-type(1)=0, padding(1), rect-count(2)=1,       */
            /* then rect: x(2) y(2) w(2) h(2) encoding(4)=0 (RAW).   */
            abHdr = new byte[4 + 12];
            abHdr[0] = 0;                        // FramebufferUpdate
            abHdr[1] = 0;                        // padding
            SRV_PutU16(abHdr, 2, 1);             // one rectangle
            SRV_PutU16(abHdr, 4, 0);             // x
            SRV_PutU16(abHdr, 6, 0);             // y
            SRV_PutU16(abHdr, 8, iW);            // width
            SRV_PutU16(abHdr, 10, iH);           // height
            SRV_PutU32(abHdr, 12, 0);            // encoding RAW

            abPixels = SRV_fb.FB_EncodeRawRect(0, 0, iW, iH, fmtClient);

            /* Header then pixels, in two writes (NoDelay is on). */
            stream.Write(abHdr, 0, abHdr.Length);
            stream.Write(abPixels, 0, abPixels.Length);

            SRV_Log("sent full update " + iW + "x" + iH + " (" + abPixels.Length + " pixel bytes)");
        }
    }
}
