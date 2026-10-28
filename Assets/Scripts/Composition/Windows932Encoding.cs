using System;
using System.Runtime.InteropServices;
using System.Text;

namespace G4Composition
{
    // Unity standalone does not always include Mono's optional I18N.CJK assembly.
    // Use Windows' installed CP932 codec with strict fallback checks.
    internal sealed class Windows932Encoding : Encoding
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern int MultiByteToWideChar(uint page, uint flags, byte[] input, int inputLength, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 5)] ushort[] output, int outputLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern int WideCharToMultiByte(uint page, uint flags, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 3)] ushort[] input, int inputLength, [Out] byte[] output, int outputLength, IntPtr defaultCharacter, [MarshalAs(UnmanagedType.Bool)] out bool usedDefault);
        static T[] Slice<T>(T[] input, int index, int count)
        {
            if (input == null || index < 0 || count < 0 || index > input.Length - count) throw new ArgumentOutOfRangeException();
            var result = new T[count]; Array.Copy(input, index, result, 0, count); return result;
        }
        // ushort is blittable on both Unity Mono and .NET; char[] marshaling differs
        // between their runtimes even when LPArray/U2 and Unicode are specified.
        static ushort[] WideChars(char[] input, int index, int count)
        {
            if (input == null || index < 0 || count < 0 || index > input.Length - count) throw new ArgumentOutOfRangeException();
            var result = new ushort[count];
            for (int i = 0; i < count; i++) result[i] = input[index + i];
            return result;
        }
        public override int GetByteCount(char[] chars, int index, int count)
        {
            if (count == 0) return 0;
            int result = WideCharToMultiByte(932, 0x400, WideChars(chars, index, count), count, null, 0, IntPtr.Zero, out bool fallback);
            if (result == 0 || fallback) throw new EncoderFallbackException("Text is not representable in the source table's CP932 encoding.");
            return result;
        }
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            int count = GetByteCount(chars, charIndex, charCount);
            if (bytes == null || byteIndex < 0 || byteIndex > bytes.Length - count) throw new ArgumentOutOfRangeException();
            if (count == 0) return 0;
            var output = new byte[count];
            int result = WideCharToMultiByte(932, 0x400, WideChars(chars, charIndex, charCount), charCount, output, count, IntPtr.Zero, out bool fallback);
            if (result != count || fallback) throw new EncoderFallbackException("CP932 conversion failed.");
            Array.Copy(output, 0, bytes, byteIndex, count); return count;
        }
        public override int GetCharCount(byte[] bytes, int index, int count)
        {
            if (count == 0) return 0;
            int result = MultiByteToWideChar(932, 8, Slice(bytes, index, count), count, null, 0);
            if (result == 0) throw new DecoderFallbackException("Invalid CP932 source text.");
            return result;
        }
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            int count = GetCharCount(bytes, byteIndex, byteCount);
            if (chars == null || charIndex < 0 || charIndex > chars.Length - count) throw new ArgumentOutOfRangeException();
            if (count == 0) return 0;
            var output = new ushort[count]; int result = MultiByteToWideChar(932, 8, Slice(bytes, byteIndex, byteCount), byteCount, output, count);
            if (result != count) throw new DecoderFallbackException("CP932 conversion failed.");
            for (int i = 0; i < count; i++) chars[charIndex + i] = (char)output[i];
            return count;
        }
        public override int GetMaxByteCount(int charCount) => checked(charCount * 2 + 2);
        public override int GetMaxCharCount(int byteCount) => checked(byteCount + 1);
    }
}
