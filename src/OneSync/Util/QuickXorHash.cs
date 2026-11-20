using System;
using System.IO;

namespace OneSync.Util;

/// <summary>
/// Microsoft's QuickXorHash, the content hash OneDrive for Business and
/// SharePoint report in file.hashes.quickXorHash.
/// </summary>
internal sealed class QuickXorHash
{
    private const int WidthInBits = 160;
    private const int Shift = 11;
    private const int BitsInLastCell = 32;

    private readonly ulong[] _data = new ulong[(WidthInBits - 1) / 64 + 1];
    private long _length;
    private int _shiftSoFar;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        int cbSize = bytes.Length;
        int vectorArrayIndex = _shiftSoFar / 64;
        int vectorOffset = _shiftSoFar % 64;
        int iterations = Math.Min(cbSize, WidthInBits);

        for (int i = 0; i < iterations; i++)
        {
            bool isLastCell = vectorArrayIndex == _data.Length - 1;
            int bitsInVectorCell = isLastCell ? BitsInLastCell : 64;

            if (vectorOffset <= bitsInVectorCell - 8)
            {
                for (int j = i; j < cbSize; j += WidthInBits)
                    _data[vectorArrayIndex] ^= (ulong)bytes[j] << vectorOffset;
            }
            else
            {
                int index1 = vectorArrayIndex;
                int index2 = isLastCell ? 0 : vectorArrayIndex + 1;
                int low = bitsInVectorCell - vectorOffset;

                byte xored = 0;
                for (int j = i; j < cbSize; j += WidthInBits)
                    xored ^= bytes[j];

                _data[index1] ^= (ulong)xored << vectorOffset;
                _data[index2] ^= (ulong)xored >> low;
            }

            vectorOffset += Shift;
            while (vectorOffset >= bitsInVectorCell)
            {
                vectorArrayIndex = isLastCell ? 0 : vectorArrayIndex + 1;
                vectorOffset -= bitsInVectorCell;
            }
        }

        _shiftSoFar = (int)((_shiftSoFar + (long)Shift * (cbSize % WidthInBits)) % WidthInBits);
        _length += cbSize;
    }

    public byte[] Finish()
    {
        var result = new byte[(WidthInBits - 1) / 8 + 1];
        for (int i = 0; i < _data.Length - 1; i++)
            Buffer.BlockCopy(BitConverter.GetBytes(_data[i]), 0, result, i * 8, 8);
        Buffer.BlockCopy(BitConverter.GetBytes(_data[^1]), 0, result,
            (_data.Length - 1) * 8, result.Length - (_data.Length - 1) * 8);

        var lengthBytes = BitConverter.GetBytes(_length);
        for (int i = 0; i < lengthBytes.Length; i++)
            result[WidthInBits / 8 - lengthBytes.Length + i] ^= lengthBytes[i];
        return result;
    }

    public static string ComputeBase64(string path)
    {
        var hash = new QuickXorHash();
        var buffer = new byte[1024 * 1024];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        int read;
        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            hash.Append(buffer.AsSpan(0, read));
        return Convert.ToBase64String(hash.Finish());
    }
}
