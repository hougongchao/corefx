// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;

namespace System.Reflection.Metadata
{
    public struct ContentId
    {
        public const int Size = 20;

        // TODO: public surface
        public readonly byte[] Guid;
        public readonly byte[] Stamp;

        public ContentId(Guid guid, int stamp)
            : this(guid.ToByteArray(), BitConverter.GetBytes(stamp))
        {
        }

        public ContentId(byte[] guid, byte[] stamp)
        {
            Debug.Assert(guid.Length == 16 && stamp.Length == 4);

            Guid = guid;
            Stamp = stamp;
        }

        public bool IsDefault => Guid == null && Stamp == null;

        internal static ContentId FromHash(ImmutableArray<byte> hashCode)
        {
            Debug.Assert(hashCode.Length >= 20);
            var guid = new byte[16];
            for (var i = 0; i < guid.Length; i++)
            {
                guid[i] = hashCode[i];
            }

            // modify the guid data so it decodes to the form of a "random" guid ala rfc4122
            var t = guid[7];
            t = (byte)((t & 0xf) | (4 << 4));
            guid[7] = t;
            t = guid[8];
            t = (byte)((t & 0x3f) | (2 << 6));
            guid[8] = t;

            // compute a random-looking stamp from the remaining bits, but with the upper bit set
            var stamp = new byte[4];
            stamp[0] = hashCode[16];
            stamp[1] = hashCode[17];
            stamp[2] = hashCode[18];
            stamp[3] = (byte)(hashCode[19] | 0x80);

            return new ContentId(guid, stamp);
        }
    }
}
