// Copyright the ProteoWizard / ms-numpress authors (github.com/ms-numpress/ms-numpress).
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Faithful line-for-line managed C# port of the canonical ms-numpress linear codec
// (MSNumpress.cpp / MSNumpress.java): optimalLinearFixedPoint, encodeFixedPoint,
// decodeFixedPoint, encodeInt, decodeInt, encodeLinear, decodeLinear. Pure-managed,
// AnyCPU (System only) — no P/Invoke, no native or x64 dependency.
//
// Decode of a numpress-linear stream may emit a phantom extrapolated leading value for
// some inputs; aligning the reconstruction to external first/last anchors is the caller's
// concern. DecodeLinear returns the raw reconstruction.

using System;

namespace ThermoRawFileParser.Writer
{
    public static class MSNumpress
    {
        // floor(0x7FFFFFFF / maxDouble), where maxDouble bounds the linear-prediction residual.
        public static double OptimalLinearFixedPoint(double[] data)
        {
            int dataSize = data.Length;
            if (dataSize == 0) return 0.0;
            if (dataSize == 1) return Math.Floor(0x7FFFFFFF / data[0]);

            double maxDouble = Math.Max(data[0], data[1]);
            for (int i = 2; i < dataSize; i++)
            {
                double extrapol = data[i - 1] + (data[i - 1] - data[i - 2]);
                double diff = data[i] - extrapol;
                maxDouble = Math.Max(maxDouble, Math.Ceiling(Math.Abs(diff) + 1));
            }
            return Math.Floor(0x7FFFFFFF / maxDouble);
        }

        // The 8-byte big-endian IEEE-754 fixed-point prefix.
        private static void EncodeFixedPoint(double fixedPoint, byte[] result)
        {
            byte[] fp = BitConverter.GetBytes(fixedPoint);
            for (int i = 0; i < 8; i++)
                result[i] = BitConverter.IsLittleEndian ? fp[7 - i] : fp[i];
        }

        private static double DecodeFixedPoint(byte[] data)
        {
            byte[] fp = new byte[8];
            for (int i = 0; i < 8; i++)
                fp[i] = BitConverter.IsLittleEndian ? data[7 - i] : data[i];
            return BitConverter.ToDouble(fp, 0);
        }

        // Encode one int (masked to 32 bits) as half-bytes: the head nibble counts leading
        // zero-nibbles (init==0 case) or, offset by 8, leading-one-nibbles (init==0xf0000000
        // case); the remaining nibbles carry the value low-to-high.
        private static int EncodeInt(int x, byte[] res, int resOffset)
        {
            int i, l;
            uint mask;
            uint ux = unchecked((uint)x);
            uint init = ux & 0xf0000000u;

            if (init == 0u)
            {
                l = 8;
                for (i = 0; i < 8; i++)
                {
                    mask = 0xf0000000u >> (4 * i);
                    if ((ux & mask) != 0u) { l = i; break; }
                }
                res[resOffset] = (byte)l;
                for (i = l; i < 8; i++)
                    res[resOffset + 1 + i - l] = (byte)(0xf & (ux >> (4 * (i - l))));
                return 1 + 8 - l;
            }
            else if (init == 0xf0000000u)
            {
                l = 7;
                for (i = 0; i < 8; i++)
                {
                    mask = 0xf0000000u >> (4 * i);
                    if ((ux & mask) != mask) { l = i; break; }
                }
                res[resOffset] = (byte)(l + 8);
                for (i = l; i < 8; i++)
                    res[resOffset + 1 + i - l] = (byte)(0xf & (ux >> (4 * (i - l))));
                return 1 + 8 - l;
            }
            else
            {
                res[resOffset] = 0;
                for (i = 0; i < 8; i++)
                    res[resOffset + 1 + i] = (byte)(0xf & (ux >> (4 * i)));
                return 9;
            }
        }

        private static int DecodeInt(byte[] data, ref int di, ref int half)
        {
            int n, i;
            uint mask, hb;
            int head;
            uint value;

            head = half == 0 ? (data[di] >> 4) : (data[di] & 0xf);
            half = 1 - half;
            if (half == 0) di++;

            if (head <= 8)
            {
                n = head;
                value = 0u;
            }
            else
            {
                n = head - 8;
                value = 0xffffffffu;
            }

            // Read the (8-n) stored low nibbles into positions 0..(7-n); for the leading-ones
            // case the high n nibbles remain 1 (value seeded to all-ones).
            for (i = 0; i < 8 - n; i++)
            {
                hb = half == 0 ? (uint)(data[di] >> 4) : (uint)(data[di] & 0xf);
                mask = 0xfu << (4 * i);
                value = (value & ~mask) | (hb << (4 * i));
                half = 1 - half;
                if (half == 0) di++;
            }
            return unchecked((int)value);
        }

        public static byte[] EncodeLinear(double[] data)
        {
            return EncodeLinear(data, OptimalLinearFixedPoint(data));
        }

        public static byte[] EncodeLinear(double[] data, double fixedPoint)
        {
            int dataSize = data.Length;
            // worst case: 8-byte fp prefix + 5 bytes per value.
            byte[] result = new byte[8 + dataSize * 5];
            long[] ints = new long[3];
            int i;
            EncodeFixedPoint(fixedPoint, result);

            if (dataSize == 0) return Trim(result, 8);

            int ri = 8;
            byte[] halfBytes = new byte[10];
            int halfByteCount = 0;
            int hbi;

            ints[1] = (long)(data[0] * fixedPoint + 0.5);
            for (i = 0; i < 4; i++)
                result[ri++] = (byte)((ints[1] >> (i * 8)) & 0xff);

            if (dataSize == 1) return Trim(result, ri);

            ints[2] = (long)(data[1] * fixedPoint + 0.5);
            for (i = 0; i < 4; i++)
                result[ri++] = (byte)((ints[2] >> (i * 8)) & 0xff);

            for (int k = 2; k < dataSize; k++)
            {
                ints[0] = ints[1];
                ints[1] = ints[2];
                ints[2] = (long)(data[k] * fixedPoint + 0.5);
                int diff = unchecked((int)(ints[2] - (ints[1] + (ints[1] - ints[0]))));

                halfByteCount += EncodeInt(diff, halfBytes, halfByteCount);

                for (hbi = 0; hbi < (halfByteCount / 2); hbi++)
                    result[ri++] = (byte)((halfBytes[2 * hbi] << 4) | (halfBytes[2 * hbi + 1] & 0xf));

                if (halfByteCount % 2 != 0)
                {
                    halfBytes[0] = halfBytes[halfByteCount - 1];
                    halfByteCount = 1;
                }
                else
                {
                    halfByteCount = 0;
                }
            }

            if (halfByteCount == 1)
                result[ri++] = (byte)(halfBytes[0] << 4);

            return Trim(result, ri);
        }

        public static double[] DecodeLinear(byte[] data)
        {
            int dataSize = data.Length;
            if (dataSize == 8) return Array.Empty<double>();
            if (dataSize < 8) throw new ArgumentException("Corrupt input data: not enough bytes for fixed point.");
            if (dataSize < 12) throw new ArgumentException("Corrupt input data: not enough bytes to read first value.");

            var result = new System.Collections.Generic.List<double>();
            double fixedPoint = DecodeFixedPoint(data);
            long[] ints = new long[3];
            int ri = 8;

            ints[1] = 0;
            for (int i = 0; i < 4; i++)
                ints[1] |= ((long)data[ri++]) << (i * 8);
            result.Add(ints[1] / fixedPoint);

            if (dataSize == 12) return result.ToArray();

            if (dataSize < 16) throw new ArgumentException("Corrupt input data: not enough bytes to read second value.");
            ints[2] = 0;
            for (int i = 0; i < 4; i++)
                ints[2] |= ((long)data[ri++]) << (i * 8);
            result.Add(ints[2] / fixedPoint);

            int half = 0;
            int di = ri;
            while (di < dataSize)
            {
                if (di == dataSize - 1 && half == 1 && (data[di] & 0xf) == 0)
                    break;

                ints[0] = ints[1];
                ints[1] = ints[2];
                int diff = DecodeInt(data, ref di, ref half);
                ints[2] = ints[1] + (ints[1] - ints[0]) + diff;
                result.Add(ints[2] / fixedPoint);
            }
            return result.ToArray();
        }

        private static byte[] Trim(byte[] src, int len)
        {
            var outp = new byte[len];
            Array.Copy(src, outp, len);
            return outp;
        }
    }
}
