using System.Buffers.Binary;

namespace NzbWebDAV.Par2Recovery
{
    /// <summary>
    /// PAR2 Reed-Solomon recovery over GF(2^16). Reconstructs missing input slices from the
    /// surviving input slices plus enough recovery slices, by solving the (square) Vandermonde
    /// system the recovery data was generated from.
    /// </summary>
    /// <remarks>
    /// Slices are interpreted as sequences of little-endian 16-bit words. Each input slice i is
    /// assigned the constant 2^n (in GF(2^16)) where n is the i-th positive integer coprime to
    /// 65535 = 3*5*17*257. A recovery slice with exponent e satisfies
    /// rec[w] = XOR over i of ( constant(i)^e * input(i)[w] ). Validated byte-exact against
    /// par2cmdline output.
    /// </remarks>
    public static class ReedSolomon
    {
        /// <summary>
        /// The first <paramref name="count"/> input-slice constants, in global slice order.
        /// </summary>
        public static ushort[] Constants(int count)
        {
            var constants = new ushort[count];
            var found = 0;
            for (var n = 1; found < count; n++)
            {
                if (n % 3 != 0 && n % 5 != 0 && n % 17 != 0 && n % 257 != 0)
                    constants[found++] = GaloisField16.Pow(2, (uint)n);
            }

            return constants;
        }

        /// <summary>
        /// Reconstructs the bytes of the missing input slices.
        /// </summary>
        /// <param name="sliceSize">Size of every slice in bytes (a multiple of 4).</param>
        /// <param name="totalSlices">Total number of input slices in the recovery set.</param>
        /// <param name="presentSlices">Global slice index -> that slice's bytes (null-padded to sliceSize).</param>
        /// <param name="missingIndices">Global indices of the slices to reconstruct.</param>
        /// <param name="recoverySlices">Available recovery slices (exponent + data). At least missingIndices.Count are required.</param>
        /// <returns>One reconstructed byte[] per entry of <paramref name="missingIndices"/>, in the same order.</returns>
        public static byte[][] Reconstruct(
            int sliceSize,
            int totalSlices,
            IReadOnlyDictionary<int, byte[]> presentSlices,
            IReadOnlyList<int> missingIndices,
            IReadOnlyList<(uint Exponent, byte[] Data)> recoverySlices)
        {
            var m = missingIndices.Count;
            if (m == 0) return [];
            if (recoverySlices.Count < m)
                throw new InvalidOperationException(
                    $"Not enough recovery slices: need {m}, have {recoverySlices.Count}.");

            var wordsPerSlice = sliceSize / 2;
            var constants = Constants(totalSlices);
            var usedRecovery = recoverySlices.Take(m).ToArray();

            // Coefficient matrix A[row=recovery, col=missing] = constant(missing)^exponent.
            var a = new ushort[m][];
            for (var row = 0; row < m; row++)
            {
                a[row] = new ushort[m];
                for (var col = 0; col < m; col++)
                    a[row][col] = GaloisField16.Pow(constants[missingIndices[col]], usedRecovery[row].Exponent);
            }

            // Right-hand side: recovery words minus the contribution of the present input slices.
            var rhs = new ushort[m][];
            for (var row = 0; row < m; row++)
            {
                var rec = ToWords(usedRecovery[row].Data, wordsPerSlice);
                foreach (var (index, bytes) in presentSlices)
                {
                    var coeff = GaloisField16.Pow(constants[index], usedRecovery[row].Exponent);
                    if (coeff == 0) continue;
                    var input = ToWords(bytes, wordsPerSlice);
                    for (var w = 0; w < wordsPerSlice; w++)
                        rec[w] ^= GaloisField16.Mul(coeff, input[w]);
                }

                rhs[row] = rec;
            }

            // Solve A * X = rhs (Gauss-Jordan over GF(2^16)). X[row] holds the words of missing slice row.
            var x = SolveInPlace(a, rhs, wordsPerSlice);

            var result = new byte[m][];
            for (var col = 0; col < m; col++)
                result[col] = ToBytes(x[col], sliceSize);
            return result;
        }

        /// <summary>
        /// Solves the square system A*X = B over GF(2^16) by Gauss-Jordan elimination.
        /// Each B[row] is a full slice's worth of words, so we eliminate over whole word-rows at once.
        /// </summary>
        private static ushort[][] SolveInPlace(ushort[][] a, ushort[][] b, int wordsPerSlice)
        {
            var m = a.Length;
            for (var col = 0; col < m; col++)
            {
                // find a non-zero pivot in this column
                var pivot = -1;
                for (var r = col; r < m; r++)
                {
                    if (a[r][col] != 0) { pivot = r; break; }
                }

                if (pivot < 0)
                    throw new InvalidOperationException("Recovery matrix is singular (insufficient/duplicate recovery slices).");

                (a[col], a[pivot]) = (a[pivot], a[col]);
                (b[col], b[pivot]) = (b[pivot], b[col]);

                // normalise the pivot row so a[col][col] == 1
                var inv = GaloisField16.Inv(a[col][col]);
                for (var c = 0; c < m; c++) a[col][c] = GaloisField16.Mul(inv, a[col][c]);
                for (var w = 0; w < wordsPerSlice; w++) b[col][w] = GaloisField16.Mul(inv, b[col][w]);

                // eliminate this column from every other row
                for (var r = 0; r < m; r++)
                {
                    if (r == col) continue;
                    var factor = a[r][col];
                    if (factor == 0) continue;
                    for (var c = 0; c < m; c++) a[r][c] ^= GaloisField16.Mul(factor, a[col][c]);
                    for (var w = 0; w < wordsPerSlice; w++) b[r][w] ^= GaloisField16.Mul(factor, b[col][w]);
                }
            }

            return b;
        }

        private static ushort[] ToWords(byte[] bytes, int wordsPerSlice)
        {
            var words = new ushort[wordsPerSlice];
            for (var w = 0; w < wordsPerSlice; w++)
                words[w] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2 * w));
            return words;
        }

        private static byte[] ToBytes(ushort[] words, int sliceSize)
        {
            var bytes = new byte[sliceSize];
            for (var w = 0; w < words.Length; w++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * w), words[w]);
            return bytes;
        }
    }
}
