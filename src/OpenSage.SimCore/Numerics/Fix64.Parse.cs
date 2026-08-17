// SIMCORE-EXEMPT: frozen F4 float boundaries (ToFloatForDisplay is float-typed by contract),
// see api-freeze-v1 F4 and design-simcore-scaffolding §1.3.
//
// The two blessed float boundaries of the deterministic core (api-freeze-v1 F4):
//   - INI decimal text  -> Fix64 : FromDecimalLiteral, integer arithmetic only, never
//     through double. Same INI bytes => same raw bits on every machine.
//   - wire float32 bits -> Fix64 : FromWireFloat, IEEE bit-pattern decomposition with
//     integer ops, never a float local. Same bits in => same raw bits out on every peer.
// Plus the one blessed display escape ToFloatForDisplay(); every crossing is greppable.

using System;

namespace OpenSage.SimCore.Numerics
{
    public readonly partial struct Fix64
    {
        /// <summary>
        /// Parses a decimal literal (optional sign, digits, optional '.', optional
        /// exponent) directly to Fix64 with round-half-up on the magnitude, using
        /// integer (Int128) arithmetic only — a double is never constructed.
        /// This is the ONLY text-to-Fix64 path (api-freeze-v1 F4).
        /// </summary>
        /// <exception cref="FormatException">The text is not a decimal literal.</exception>
        /// <exception cref="OverflowException">The value does not fit Q31.32.</exception>
        public static Fix64 FromDecimalLiteral(ReadOnlySpan<char> text)
        {
            text = text.Trim();
            if (text.IsEmpty)
            {
                throw new FormatException("Empty Fix64 literal.");
            }

            var sign = 1;
            if (text[0] == '+' || text[0] == '-')
            {
                if (text[0] == '-')
                {
                    sign = -1;
                }
                text = text[1..];
            }

            // Split off an optional exponent.
            var exponent = 0;
            var eIndex = text.IndexOfAny('e', 'E');
            if (eIndex >= 0)
            {
                var expText = text[(eIndex + 1)..];
                text = text[..eIndex];
                if (!int.TryParse(expText, System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out exponent))
                {
                    throw new FormatException("Malformed exponent in Fix64 literal.");
                }
            }

            // Split mantissa into integer and fraction digit runs.
            var dot = text.IndexOf('.');
            ReadOnlySpan<char> intDigits, fracDigits;
            if (dot >= 0)
            {
                intDigits = text[..dot];
                fracDigits = text[(dot + 1)..];
            }
            else
            {
                intDigits = text;
                fracDigits = default;
            }

            if (intDigits.IsEmpty && fracDigits.IsEmpty)
            {
                throw new FormatException("Fix64 literal has no digits.");
            }
            ValidateDigits(intDigits);
            ValidateDigits(fracDigits);

            // Apply the exponent as a pure digit shift: build the combined digit string and
            // move the virtual decimal point by 'exponent' places.
            // pointPos = number of digits left of the point within 'digits'.
            Span<char> digits = stackalloc char[intDigits.Length + fracDigits.Length];
            intDigits.CopyTo(digits);
            fracDigits.CopyTo(digits[intDigits.Length..]);
            var pointPos = intDigits.Length + exponent;

            // Integer part: digits[0 .. pointPos), right-padded with zeros if the point
            // moved past the end.
            Int128 intPart = 0;
            for (var i = 0; i < pointPos; i++)
            {
                var d = i < digits.Length ? digits[i] - '0' : 0;
                intPart = intPart * 10 + d;
                if (intPart > (Int128)long.MaxValue)
                {
                    throw new OverflowException("Fix64 literal out of range.");
                }
            }

            // Fraction part: digits from max(pointPos, 0) on, with -pointPos leading zeros
            // if the point moved left past the first digit.
            var leadingZeros = pointPos < 0 ? -pointPos : 0;
            var fracStart = pointPos < 0 ? 0 : pointPos;
            var fracCount = digits.Length - fracStart;
            if (fracCount < 0)
            {
                fracCount = 0;   // exponent moved the point past the last digit
            }
            Span<int> frac = stackalloc int[leadingZeros + fracCount];
            for (var i = 0; i < fracCount; i++)
            {
                frac[leadingZeros + i] = digits[fracStart + i] - '0';
            }

            // raw = sign * ( I*2^32 + round_half_up(0.F * 2^32) ), all pure integer and
            // EXACT for any number of fraction digits: extract 32 binary fraction bits by
            // repeatedly doubling the decimal digit string (carry out of the leading digit
            // is the next bit), then one more doubling decides the round-half-up bit —
            // an exactly-half remainder carries with all zeros behind it, rounding up.
            long fracRaw = 0;
            for (var bit = 0; bit <= FRACTIONAL_PLACES; bit++)
            {
                var carry = 0;
                for (var i = frac.Length - 1; i >= 0; i--)
                {
                    var doubled = frac[i] * 2 + carry;
                    frac[i] = doubled % 10;
                    carry = doubled / 10;
                }
                fracRaw = (fracRaw << 1) | carry;
            }
            fracRaw = (fracRaw >> 1) + (fracRaw & 1);   // 33rd bit is the rounding bit

            var raw = ((intPart << FRACTIONAL_PLACES) + fracRaw) * sign;

            if (raw > long.MaxValue || raw < long.MinValue)
            {
                throw new OverflowException("Fix64 literal out of range.");
            }
            return new Fix64((long)raw);
        }

        private static void ValidateDigits(ReadOnlySpan<char> digits)
        {
            foreach (var c in digits)
            {
                if (c < '0' || c > '9')
                {
                    throw new FormatException("Malformed Fix64 literal.");
                }
            }
        }

        /// <summary>
        /// Converts the bit pattern of an IEEE-754 binary32 (as carried on the wire in
        /// order payloads) to Fix64 by integer decomposition of sign/exponent/mantissa —
        /// no float is ever constructed. Truncates toward zero below 2^-32; saturates
        /// to MinValue/MaxValue beyond the Q31.32 range (±infinity included).
        /// This is the ONLY wire-float-to-Fix64 path (api-freeze-v1 F4).
        /// </summary>
        /// <exception cref="ArgumentException">The bits encode a NaN (malformed input).</exception>
        public static Fix64 FromWireFloat(uint ieeeBits)
        {
            var negative = (ieeeBits >> 31) != 0;
            var biasedExp = (int)((ieeeBits >> 23) & 0xFF);
            var mantissa = (long)(ieeeBits & 0x7FFFFF);

            if (biasedExp == 0xFF)
            {
                if (mantissa != 0)
                {
                    throw new ArgumentException("NaN float32 bits are malformed sim input.", nameof(ieeeBits));
                }
                return negative ? MinValue : MaxValue;   // ±infinity saturates
            }

            if (biasedExp == 0)
            {
                if (mantissa == 0)
                {
                    return Zero;                          // ±0
                }
                biasedExp = 1;                            // denormal: no implicit bit
            }
            else
            {
                mantissa |= 1L << 23;                     // normal: implicit leading 1
            }

            // value = mantissa * 2^(biasedExp - 127 - 23); raw = value * 2^32.
            var shift = biasedExp - 127 - 23 + FRACTIONAL_PLACES;

            long raw;
            if (shift >= 40)
            {
                // mantissa < 2^24, so a shift of 40+ overflows the long raw: saturate.
                return negative ? MinValue : MaxValue;
            }
            if (shift >= 0)
            {
                raw = mantissa << shift;
            }
            else if (shift > -64)
            {
                raw = mantissa >> -shift;                 // truncate toward zero (mantissa > 0)
            }
            else
            {
                raw = 0;
            }

            return new Fix64(negative ? -raw : raw);
        }

        /// <summary>
        /// The one blessed escape to float, for rendering/UI display only. Never feed the
        /// result back into sim state (api-freeze-v1 F4; the analyzer polices call sites).
        /// </summary>
        public float ToFloatForDisplay()
        {
            return (float)(m_rawValue / 4294967296.0);
        }
    }
}
