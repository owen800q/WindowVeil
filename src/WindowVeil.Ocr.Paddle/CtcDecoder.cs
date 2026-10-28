using System.Text;

namespace WindowVeil.Ocr.Paddle;

/// <summary>
/// The recognition character table of PaddleX <c>CTCLabelDecode</c>: index 0 is the CTC blank, indices 1..N are the
/// dictionary entries in file order and, when <c>use_space_char</c> is true (the official default), index N+1 is a space.
/// The model's class count must therefore equal <see cref="ClassCount"/>.
/// </summary>
public sealed class CharacterDictionary
{
    private readonly string[] _table;

    /// <summary>Creates the table from dictionary entries (as listed in <c>PostProcess.character_dict</c>).</summary>
    public CharacterDictionary(IReadOnlyList<string> entries, bool useSpaceChar = true)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) throw new ArgumentException("The character dictionary is empty.", nameof(entries));
        Entries = entries.ToArray();
        UseSpaceChar = useSpaceChar;
        var table = new List<string>(entries.Count + 2) { "" }; // blank, never emitted
        table.AddRange(Entries);
        if (useSpaceChar) table.Add(" ");
        _table = table.ToArray();
    }

    /// <summary>Dictionary entries in file order (without blank and space).</summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>Whether a space class is appended after the dictionary.</summary>
    public bool UseSpaceChar { get; }

    /// <summary>Number of model output classes this dictionary implies: entries + blank (+ space).</summary>
    public int ClassCount => _table.Length;

    /// <summary>Text for a class index (empty for the blank).</summary>
    public string this[int index] => _table[index];
}

/// <summary>Greedy CTC decoding exactly like PaddleX <c>CTCLabelDecode</c> (<c>is_remove_duplicate=True</c>).</summary>
public static class CtcDecoder
{
    /// <summary>
    /// Decodes one sequence of class probabilities [T, C]: per-step argmax (first maximum on ties) and max probability; a step
    /// is kept when its index differs from the previous step's and is not the blank (0). Confidence is the float32 mean of
    /// the kept probabilities, or 0 when nothing is kept.
    /// </summary>
    public static (string Text, float Confidence) Decode(ReadOnlySpan<float> probs, int steps, int classes, CharacterDictionary dictionary)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        if (classes != dictionary.ClassCount)
            throw new ArgumentException($"The model has {classes} classes but the dictionary implies {dictionary.ClassCount}.", nameof(classes));
        if (probs.Length < steps * classes) throw new ArgumentException("Probability buffer is too small.", nameof(probs));
        var sb = new StringBuilder();
        var kept = new List<float>();
        int prev = -1;
        for (int t = 0; t < steps; t++)
        {
            var row = probs.Slice(t * classes, classes);
            int best = 0;
            float max = row[0];
            for (int c = 1; c < classes; c++)
            {
                if (row[c] > max) { max = row[c]; best = c; }
            }
            bool keep = (t == 0 || best != prev) && best != 0;
            prev = best;
            if (!keep) continue;
            sb.Append(dictionary[best]);
            kept.Add(max);
        }
        return (sb.ToString(), kept.Count == 0 ? 0f : MeanFloat32(kept));
    }

    /// <summary>NumPy float32 <c>mean</c>: pairwise summation in float32 (blocks of 8 lanes below 128 elements), then division.</summary>
    internal static float MeanFloat32(List<float> values)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values);
        return PairwiseSum(span) / span.Length;
    }

    private static float PairwiseSum(ReadOnlySpan<float> a)
    {
        int n = a.Length;
        if (n < 8)
        {
            float res = 0f;
            for (int i = 0; i < n; i++) res += a[i];
            return res;
        }
        if (n <= 128)
        {
            Span<float> r = stackalloc float[8];
            for (int j = 0; j < 8; j++) r[j] = a[j];
            int i;
            for (i = 8; i < n - (n % 8); i += 8)
                for (int j = 0; j < 8; j++) r[j] += a[i + j];
            float res = ((r[0] + r[1]) + (r[2] + r[3])) + ((r[4] + r[5]) + (r[6] + r[7]));
            for (; i < n; i++) res += a[i];
            return res;
        }
        int n2 = n / 2;
        n2 -= n2 % 8;
        return PairwiseSum(a[..n2]) + PairwiseSum(a[n2..]);
    }
}
