/*-----------------------------------------------------------------------------*/
/* SPDX-License-Identifier: GPL-2.0-or-later                                   */
/* Copyright (c) 2026 B. C. Services                                           */
/*                                                                             */
/* Licensed under the GNU General Public License version 2 or later --         */
/* see LICENSE beside this source.  GPL-2.0 full text in GPL-2.0.txt.          */
/*-----------------------------------------------------------------------------*/
/* Des.cs:                                                                     */
/*                                                                             */
/* Computes the expected VNC-Authentication response so a server can verify    */
/* what a client sends: the DES block cipher plus the VNC key bit-reversal     */
/* quirk, from the published FIPS 46-3 specification.  One block runs twice    */
/* per login, so the one-bit-per-int representation is chosen for clarity over */
/* speed.                                                                      */
/*                                                                             */
/* Not a general crypto library.  DES is obsolete; it is here only because it  */
/* is the credential exchange VNC clients speak, on a LAN-trusted link.        */
/*-----------------------------------------------------------------------------*/

using System;

namespace Des
{
    /*------------------------------------------------------------------------*/
    /* clsDes:                                                                */
    /*                                                                        */
    /* Holds the 16 expanded round subkeys for one 8-byte key and exposes the */
    /* block encrypt plus the VNC response helper.  Instances are cheap and   */
    /* single-use.                                                            */
    /*------------------------------------------------------------------------*/

    public sealed class clsDes
    {
        private readonly int[][] DES_aaiSubkeys;   // [16][48] round subkeys, MSB-first bits

        /*--------------------------------------------------------------------*/
        /* FIPS 46-3 constant tables.                                         */
        /*--------------------------------------------------------------------*/

        private static readonly int[] DES_aiIP =
        {
            58, 50, 42, 34, 26, 18, 10,  2,  60, 52, 44, 36, 28, 20, 12,  4,
            62, 54, 46, 38, 30, 22, 14,  6,  64, 56, 48, 40, 32, 24, 16,  8,
            57, 49, 41, 33, 25, 17,  9,  1,  59, 51, 43, 35, 27, 19, 11,  3,
            61, 53, 45, 37, 29, 21, 13,  5,  63, 55, 47, 39, 31, 23, 15,  7
        };

        private static readonly int[] DES_aiFP =
        {
            40,  8, 48, 16, 56, 24, 64, 32,  39,  7, 47, 15, 55, 23, 63, 31,
            38,  6, 46, 14, 54, 22, 62, 30,  37,  5, 45, 13, 53, 21, 61, 29,
            36,  4, 44, 12, 52, 20, 60, 28,  35,  3, 43, 11, 51, 19, 59, 27,
            34,  2, 42, 10, 50, 18, 58, 26,  33,  1, 41,  9, 49, 17, 57, 25
        };

        private static readonly int[] DES_aiE =
        {
            32,  1,  2,  3,  4,  5,   4,  5,  6,  7,  8,  9,   8,  9, 10, 11, 12, 13,
            12, 13, 14, 15, 16, 17,  16, 17, 18, 19, 20, 21,  20, 21, 22, 23, 24, 25,
            24, 25, 26, 27, 28, 29,  28, 29, 30, 31, 32,  1
        };

        private static readonly int[] DES_aiP =
        {
            16,  7, 20, 21, 29, 12, 28, 17,   1, 15, 23, 26,  5, 18, 31, 10,
             2,  8, 24, 14, 32, 27,  3,  9,  19, 13, 30,  6, 22, 11,  4, 25
        };

        private static readonly int[] DES_aiPC1 =
        {
            57, 49, 41, 33, 25, 17,  9,   1, 58, 50, 42, 34, 26, 18,
            10,  2, 59, 51, 43, 35, 27,  19, 11,  3, 60, 52, 44, 36,
            63, 55, 47, 39, 31, 23, 15,   7, 62, 54, 46, 38, 30, 22,
            14,  6, 61, 53, 45, 37, 29,  21, 13,  5, 28, 20, 12,  4
        };

        private static readonly int[] DES_aiPC2 =
        {
            14, 17, 11, 24,  1,  5,   3, 28, 15,  6, 21, 10,
            23, 19, 12,  4, 26,  8,  16,  7, 27, 20, 13,  2,
            41, 52, 31, 37, 47, 55,  30, 40, 51, 45, 33, 48,
            44, 49, 39, 56, 34, 53,  46, 42, 50, 36, 29, 32
        };

        private static readonly int[] DES_aiShifts =
        {
            1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1
        };

        private static readonly int[][] DES_aaiS =
        {
            new int[]
            {
                14,  4, 13,  1,  2, 15, 11,  8,  3, 10,  6, 12,  5,  9,  0,  7,
                 0, 15,  7,  4, 14,  2, 13,  1, 10,  6, 12, 11,  9,  5,  3,  8,
                 4,  1, 14,  8, 13,  6,  2, 11, 15, 12,  9,  7,  3, 10,  5,  0,
                15, 12,  8,  2,  4,  9,  1,  7,  5, 11,  3, 14, 10,  0,  6, 13
            },
            new int[]
            {
                15,  1,  8, 14,  6, 11,  3,  4,  9,  7,  2, 13, 12,  0,  5, 10,
                 3, 13,  4,  7, 15,  2,  8, 14, 12,  0,  1, 10,  6,  9, 11,  5,
                 0, 14,  7, 11, 10,  4, 13,  1,  5,  8, 12,  6,  9,  3,  2, 15,
                13,  8, 10,  1,  3, 15,  4,  2, 11,  6,  7, 12,  0,  5, 14,  9
            },
            new int[]
            {
                10,  0,  9, 14,  6,  3, 15,  5,  1, 13, 12,  7, 11,  4,  2,  8,
                13,  7,  0,  9,  3,  4,  6, 10,  2,  8,  5, 14, 12, 11, 15,  1,
                13,  6,  4,  9,  8, 15,  3,  0, 11,  1,  2, 12,  5, 10, 14,  7,
                 1, 10, 13,  0,  6,  9,  8,  7,  4, 15, 14,  3, 11,  5,  2, 12
            },
            new int[]
            {
                 7, 13, 14,  3,  0,  6,  9, 10,  1,  2,  8,  5, 11, 12,  4, 15,
                13,  8, 11,  5,  6, 15,  0,  3,  4,  7,  2, 12,  1, 10, 14,  9,
                10,  6,  9,  0, 12, 11,  7, 13, 15,  1,  3, 14,  5,  2,  8,  4,
                 3, 15,  0,  6, 10,  1, 13,  8,  9,  4,  5, 11, 12,  7,  2, 14
            },
            new int[]
            {
                 2, 12,  4,  1,  7, 10, 11,  6,  8,  5,  3, 15, 13,  0, 14,  9,
                14, 11,  2, 12,  4,  7, 13,  1,  5,  0, 15, 10,  3,  9,  8,  6,
                 4,  2,  1, 11, 10, 13,  7,  8, 15,  9, 12,  5,  6,  3,  0, 14,
                11,  8, 12,  7,  1, 14,  2, 13,  6, 15,  0,  9, 10,  4,  5,  3
            },
            new int[]
            {
                12,  1, 10, 15,  9,  2,  6,  8,  0, 13,  3,  4, 14,  7,  5, 11,
                10, 15,  4,  2,  7, 12,  9,  5,  6,  1, 13, 14,  0, 11,  3,  8,
                 9, 14, 15,  5,  2,  8, 12,  3,  7,  0,  4, 10,  1, 13, 11,  6,
                 4,  3,  2, 12,  9,  5, 15, 10, 11, 14,  1,  7,  6,  0,  8, 13
            },
            new int[]
            {
                 4, 11,  2, 14, 15,  0,  8, 13,  3, 12,  9,  7,  5, 10,  6,  1,
                13,  0, 11,  7,  4,  9,  1, 10, 14,  3,  5, 12,  2, 15,  8,  6,
                 1,  4, 11, 13, 12,  3,  7, 14, 10, 15,  6,  8,  0,  5,  9,  2,
                 6, 11, 13,  8,  1,  4, 10,  7,  9,  5,  0, 15, 14,  2,  3, 12
            },
            new int[]
            {
                13,  2,  8,  4,  6, 15, 11,  1, 10,  9,  3, 14,  5,  0, 12,  7,
                 1, 15, 13,  8, 10,  3,  7,  4, 12,  5,  6, 11,  0, 14,  9,  2,
                 7, 11,  4,  1,  9, 12, 14,  2,  0,  6, 10, 13, 15,  3,  5,  8,
                 2,  1, 14,  7,  4, 10,  8, 13, 15, 12,  9,  0,  3,  5,  6, 11
            }
        };

        /*------------------------------------------------------------------------*/
        /* clsDes (constructor):                                                  */
        /*                                                                        */
        /* Runs the DES key schedule for the given 8-byte key, filling the 16     */
        /* round subkeys.  The key's parity bits are ignored, as in standard      */
        /* DES.                                                                   */
        /*                                                                        */
        /* Arguments:                                                             */
        /*     abKey8 : exactly 8 key bytes (already VNC-reversed by the caller   */
        /*              when used for VNC auth).                                  */
        /*                                                                        */
        /* Returns:                                                               */
        /*     clsDes : an instance whose subkeys are ready for DES_EncryptBlock. */
        /*------------------------------------------------------------------------*/

        public clsDes(byte[] abKey8)
        {
            int[] aiKeyBits;                     // 64 raw key bits
            int[] aiPc1;                         // 56 bits after PC-1
            int[] aiC;                           // left half of the schedule
            int[] aiD;                           // right half of the schedule
            int[] aiCD;                          // recombined halves before PC-2
            int   iRound;
            int   i;

            DES_aaiSubkeys = new int[16][];

            aiKeyBits = new int[64];
            aiPc1     = new int[56];
            aiC       = new int[28];
            aiD       = new int[28];
            aiCD      = new int[56];

            /* Expand the key bytes to bits, then apply PC-1. */
            DES_BytesToBits(abKey8, 8, aiKeyBits);
            DES_Permute(aiKeyBits, aiPc1, DES_aiPC1, 56);

            /* Split into the two 28-bit halves. */
            for (i = 0; i < 28; i++)
            {   /* First 28 bits are C, next 28 are D. */
                aiC[i] = aiPc1[i];
                aiD[i] = aiPc1[i + 28];
            }

            for (iRound = 0; iRound < 16; iRound++)
            {   /* Each round rotates both halves, then selects a subkey via PC-2. */
                DES_aaiSubkeys[iRound] = new int[48];

                /* Rotate C and D left by this round's amount. */
                DES_RotateLeft28(aiC, DES_aiShifts[iRound]);
                DES_RotateLeft28(aiD, DES_aiShifts[iRound]);

                /* Recombine and select. */
                for (i = 0; i < 28; i++)
                {   /* C low, D high. */
                    aiCD[i]      = aiC[i];
                    aiCD[i + 28] = aiD[i];
                }

                DES_Permute(aiCD, DES_aaiSubkeys[iRound], DES_aiPC2, 48);
            }
        }

        /*--------------------------------------------------------------------*/
        /* DES_Permute:                                                       */
        /*                                                                    */
        /* Generic bit permutation: aiOut[i] = aiIn[table[i] - 1].  Local.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     aiIn    : source bit array.                                    */
        /*     aiOut   : destination bit array of iOutLen elements.           */
        /*     aiTable : 1-based source positions.                            */
        /*     iOutLen : number of output bits.                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : fills aiOut.                                            */
        /*--------------------------------------------------------------------*/

        private static void DES_Permute(int[] aiIn, int[] aiOut, int[] aiTable, int iOutLen)
        {
            int i;

            for (i = 0; i < iOutLen; i++)
            {   /* Table entries are 1-based positions into the input. */
                aiOut[i] = aiIn[aiTable[i] - 1];
            }
        }

        /*--------------------------------------------------------------------*/
        /* DES_BytesToBits:                                                   */
        /*                                                                    */
        /* Expands bytes to bits, MSB first within each byte.  Local.         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     abBytes    : source bytes.                                     */
        /*     iNumBytes  : count to expand.                                  */
        /*     aiBits     : destination of iNumBytes*8 ints.                  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : fills aiBits.                                           */
        /*--------------------------------------------------------------------*/

        private static void DES_BytesToBits(byte[] abBytes, int iNumBytes, int[] aiBits)
        {
            int i;
            int j;

            for (i = 0; i < iNumBytes; i++)
            {   /* High bit first. */
                for (j = 0; j < 8; j++)
                {   /* Shift the wanted bit down and mask. */
                    aiBits[(i * 8) + j] = (abBytes[i] >> (7 - j)) & 0x01;
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* DES_BitsToBytes:                                                   */
        /*                                                                    */
        /* Packs bits back into bytes, MSB first.  Local.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     aiBits    : source bit array.                                  */
        /*     iNumBytes : bytes to produce.                                  */
        /*     abBytes   : destination bytes.                                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : fills abBytes.                                          */
        /*--------------------------------------------------------------------*/

        private static void DES_BitsToBytes(int[] aiBits, int iNumBytes, byte[] abBytes)
        {
            int i;
            int j;
            int iByte;

            for (i = 0; i < iNumBytes; i++)
            {   /* Rebuild each byte high bit first. */
                iByte = 0;

                for (j = 0; j < 8; j++)
                {   /* OR the bit into place. */
                    iByte = (iByte << 1) | (aiBits[(i * 8) + j] & 0x01);
                }

                abBytes[i] = (byte)iByte;
            }
        }

        /*--------------------------------------------------------------------*/
        /* DES_RotateLeft28:                                                  */
        /*                                                                    */
        /* Rotates a 28-bit half left by iCount, in place.  Local.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     aiHalf : 28-element bit array, modified in place.              */
        /*     iCount : rotate amount (1 or 2).                               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : rotates aiHalf.                                         */
        /*--------------------------------------------------------------------*/

        private static void DES_RotateLeft28(int[] aiHalf, int iCount)
        {
            int[] aiTmp;                         // rotated copy
            int   i;

            aiTmp = new int[28];

            for (i = 0; i < 28; i++)
            {   /* Element i comes from (i + iCount) modulo 28. */
                aiTmp[i] = aiHalf[(i + iCount) % 28];
            }

            Array.Copy(aiTmp, aiHalf, 28);
        }

        /*---------------------------------------------------------------------*/
        /* DES_Feistel:                                                        */
        /*                                                                     */
        /* The DES round function f(R, K): expand, XOR subkey, S-box, permute. */
        /* Local.                                                              */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     aiRight  : 32-bit right half.                                   */
        /*     aiSubkey : 48-bit round subkey.                                 */
        /*     aiOut32  : 32-bit result.                                       */
        /*                                                                     */
        /* Returns:                                                            */
        /*     void : fills aiOut32.                                           */
        /*---------------------------------------------------------------------*/

        private static void DES_Feistel(int[] aiRight, int[] aiSubkey, int[] aiOut32)
        {
            int[] aiExpanded;                    // R after E
            int[] aiSboxIn;                      // expanded XOR subkey
            int[] aiSboxOut;                     // S-box output
            int   iGroup;
            int   iRow;
            int   iCol;
            int   iVal;
            int   j;

            aiExpanded = new int[48];
            aiSboxIn   = new int[48];
            aiSboxOut  = new int[32];

            /* Expand the right half to 48 bits. */
            DES_Permute(aiRight, aiExpanded, DES_aiE, 48);

            /* XOR the round subkey. */
            for (j = 0; j < 48; j++)
            {   /* Bitwise XOR. */
                aiSboxIn[j] = aiExpanded[j] ^ aiSubkey[j];
            }

            for (iGroup = 0; iGroup < 8; iGroup++)
            {   /* Each 6-bit group indexes one S-box. */
                iRow = (aiSboxIn[iGroup * 6 + 0] << 1) | aiSboxIn[iGroup * 6 + 5];
                iCol = (aiSboxIn[iGroup * 6 + 1] << 3) |
                       (aiSboxIn[iGroup * 6 + 2] << 2) |
                       (aiSboxIn[iGroup * 6 + 3] << 1) |
                       (aiSboxIn[iGroup * 6 + 4]);

                iVal = DES_aaiS[iGroup][(iRow * 16) + iCol];

                for (j = 0; j < 4; j++)
                {   /* Emit the 4 value bits, MSB first. */
                    aiSboxOut[(iGroup * 4) + j] = (iVal >> (3 - j)) & 0x01;
                }
            }

            /* Permute the S-box output with P. */
            DES_Permute(aiSboxOut, aiOut32, DES_aiP, 32);
        }

        /*----------------------------------------------------------------------*/
        /* DES_EncryptBlock:                                                    */
        /*                                                                      */
        /* Standard 16-round DES block encryption with this instance's subkeys. */
        /*                                                                      */
        /* Arguments:                                                           */
        /*     abIn8  : 8 plaintext bytes.                                      */
        /*     abOut8 : 8-byte ciphertext buffer.                               */
        /*                                                                      */
        /* Returns:                                                             */
        /*     void : writes 8 bytes to abOut8.                                 */
        /*----------------------------------------------------------------------*/

        public void DES_EncryptBlock(byte[] abIn8, byte[] abOut8)
        {
            int[] aiInBits;                      // plaintext bits
            int[] aiPermuted;                    // after IP
            int[] aiLeft;                        // left half
            int[] aiRight;                       // right half
            int[] aiF;                           // round-function output
            int[] aiTmp;                         // old right half
            int[] aiPreOut;                      // R16 || L16
            int   iRound;
            int   j;

            aiInBits   = new int[64];
            aiPermuted = new int[64];
            aiLeft     = new int[32];
            aiRight    = new int[32];
            aiF        = new int[32];
            aiTmp      = new int[32];
            aiPreOut   = new int[64];

            /* Load and apply the initial permutation. */
            DES_BytesToBits(abIn8, 8, aiInBits);
            DES_Permute(aiInBits, aiPermuted, DES_aiIP, 64);

            /* Split into L0 and R0. */
            for (j = 0; j < 32; j++)
            {   /* First 32 bits are L, next 32 are R. */
                aiLeft[j]  = aiPermuted[j];
                aiRight[j] = aiPermuted[j + 32];
            }

            for (iRound = 0; iRound < 16; iRound++)
            {   /* L(i) = R(i-1); R(i) = L(i-1) XOR f(R(i-1), K(i)). */
                Array.Copy(aiRight, aiTmp, 32);

                DES_Feistel(aiRight, DES_aaiSubkeys[iRound], aiF);

                for (j = 0; j < 32; j++)
                {   /* New right = old left XOR f(). */
                    aiRight[j] = aiLeft[j] ^ aiF[j];
                }

                Array.Copy(aiTmp, aiLeft, 32);
            }

            /* Pre-output is R16 || L16 (swapped), then FP, then pack. */
            for (j = 0; j < 32; j++)
            {   /* Right half first. */
                aiPreOut[j]      = aiRight[j];
                aiPreOut[j + 32] = aiLeft[j];
            }

            DES_Permute(aiPreOut, aiInBits, DES_aiFP, 64);
            DES_BitsToBytes(aiInBits, 8, abOut8);
        }

        /*---------------------------------------------------------------------*/
        /* DES_ReverseByteBits:                                                */
        /*                                                                     */
        /* Reverses the bit order of one byte - the VNC key transform.  Local. */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     bIn : byte to reverse.                                          */
        /*                                                                     */
        /* Returns:                                                            */
        /*     byte : the bit-reversed byte.                                   */
        /*---------------------------------------------------------------------*/

        private static byte DES_ReverseByteBits(byte bIn)
        {
            int iOut;                            // reversed accumulator
            int j;

            iOut = 0;

            for (j = 0; j < 8; j++)
            {   /* Bit j -> bit (7-j). */
                if ((bIn & (1 << j)) != 0)
                {   /* Source bit set: set the mirrored bit. */
                    iOut = iOut | (1 << (7 - j));
                }
            }

            /* The byte with its bit order reversed. */
            return((byte)iOut);
        }

        /*---------------------------------------------------------------------*/
        /* DES_VncEncrypt:                                                     */
        /*                                                                     */
        /* Computes the 16-byte VNC-Authentication response for a password and */
        /* a 16-byte challenge: build the key from the first 8 password bytes  */
        /* (NUL-padded, each bit-reversed), then DES-ECB the two challenge     */
        /* halves.  The server calls this to get the response it expects.      */
        /*                                                                     */
        /* Arguments:                                                          */
        /*     sPassword    : the password string.                             */
        /*     abChallenge  : the 16-byte challenge.                           */
        /*                                                                     */
        /* Returns:                                                            */
        /*     byte[] : the 16-byte expected response.                         */
        /*---------------------------------------------------------------------*/

        public static byte[] DES_VncEncrypt(string sPassword, byte[] abChallenge)
        {
            byte[] abKey;   // bit-reversed DES key
            byte[] abResp;  // 16-byte response
            byte[] abHalf;  // one 8-byte block out
            byte[] abPass;  // password as bytes
            clsDes  des;    // schedule for the key
            int    i;

            abKey  = new byte[8];
            abResp = new byte[16];
            abHalf = new byte[8];

            /* Latin-1 style byte view of the password (VNC treats it as bytes). */
            abPass = new byte[sPassword.Length];
            for (i = 0; i < sPassword.Length; i++)
            {   /* Keep the low 8 bits of each character. */
                abPass[i] = (byte)(sPassword[i] & 0xFF);
            }

            /* Build the key: first 8 bytes, NUL-padded, each bit-reversed. */
            for (i = 0; i < 8; i++)
            {   /* Use the password byte if present, else 0. */
                byte bSrc;

                if (i < abPass.Length)
                {   /* Real password byte. */
                    bSrc = abPass[i];
                }
                else
                {   /* Past the end: pad. */
                    bSrc = 0;
                }

                abKey[i] = DES_ReverseByteBits(bSrc);
            }

            /* Schedule once, encrypt both challenge halves ECB. */
            des = new clsDes(abKey);

            Array.Copy(abChallenge, 0, abHalf, 0, 8);
            des.DES_EncryptBlock(abHalf, abHalf);
            Array.Copy(abHalf, 0, abResp, 0, 8);

            Array.Copy(abChallenge, 8, abHalf, 0, 8);
            des.DES_EncryptBlock(abHalf, abHalf);
            Array.Copy(abHalf, 0, abResp, 8, 8);

            /* The expected 16-byte VNC response. */
            return(abResp);
        }
    }
}
