// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Buffers.Binary;

namespace XboxDevCLI.Services;

internal static class PngHeader
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] Ihdr = [(byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    internal static bool TryReadDimensions(ReadOnlySpan<byte> header, out uint width, out uint height)
    {
        width = height = 0;
        if (header.Length < 33 ||
            !header[..8].SequenceEqual(Signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8, 4)) != 13 ||
            !header.Slice(12, 4).SequenceEqual(Ihdr))
        {
            return false;
        }

        width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        return width > 0 && height > 0;
    }
}
