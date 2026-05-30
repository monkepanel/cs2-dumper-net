using System.Buffers.Binary;
using System.Text;

namespace Cs2Dumper.Pe;

public sealed class PatternException(string message) : Exception(message);

/// <summary>Pattern atoms — a faithful port of pelite's <c>pattern::Atom</c>.</summary>
public enum AtomKind : byte
{
    Byte, Save, Push, Pop, Fuzzy, Skip, Back, Rangext, Many,
    Jump1, Jump4, Ptr, Pir, VTypeName, Check, Aligned,
    ReadI8, ReadU8, ReadI16, ReadU16, ReadI32, ReadU32, Zero,
    Case, Break, Nop,
}

public readonly record struct Atom(AtomKind Kind, byte Arg = 0);

/// <summary>
/// Parses pelite's pattern string syntax into atoms. Direct port of pelite's
/// <c>pattern::parse</c> (proc-macros/pattern.rs), including the <c>Save(0)</c> prefix,
/// the <c>{</c>/<c>}</c> jump-to-push rewrite, <c>?</c> coalescing and redundant-tail trimming.
/// </summary>
public static class PatternParser
{
    private const byte PtrSkip = 0;

    private sealed class SubPattern
    {
        public int Case;
        public readonly List<int> Brks = new();
        public int Save;
        public int SaveNext;
        public int Depth;
    }

    public static Atom[] Parse(string pattern)
    {
        byte[] pat = Encoding.ASCII.GetBytes(pattern);
        var result = new List<Atom> { new(AtomKind.Save, 0) };
        int i = 0;
        int save = 1;
        int depth = 0;
        var subs = new List<SubPattern>();

        while (i < pat.Length)
        {
            byte chr = pat[i++];
            switch (chr)
            {
                case (byte)'%':
                    result.Add(new Atom(AtomKind.Jump1));
                    break;
                case (byte)'$':
                    result.Add(new Atom(AtomKind.Jump4));
                    break;
                case (byte)'*':
                    result.Add(new Atom(AtomKind.Ptr));
                    break;
                case (byte)'{':
                {
                    depth++;
                    Atom last = result[^1];
                    Atom moved;
                    switch (last.Kind)
                    {
                        case AtomKind.Jump1:
                            result[^1] = new Atom(AtomKind.Push, 1);
                            moved = new Atom(AtomKind.Jump1);
                            break;
                        case AtomKind.Jump4:
                            result[^1] = new Atom(AtomKind.Push, 4);
                            moved = new Atom(AtomKind.Jump4);
                            break;
                        case AtomKind.Ptr:
                            result[^1] = new Atom(AtomKind.Push, PtrSkip);
                            moved = new Atom(AtomKind.Ptr);
                            break;
                        default:
                            throw new PatternException("stack must follow jump");
                    }

                    result.Add(moved);
                    break;
                }
                case (byte)'}':
                    if (depth <= 0)
                    {
                        throw new PatternException("stack unbalanced");
                    }

                    depth--;
                    result.Add(new Atom(AtomKind.Pop));
                    break;
                case (byte)'(':
                {
                    var sub = new SubPattern { Save = save, Depth = depth, Case = result.Count };
                    subs.Add(sub);
                    result.Add(new Atom(AtomKind.Case, 0));
                    break;
                }
                case (byte)'|':
                {
                    if (subs.Count == 0)
                    {
                        throw new PatternException("sub pattern error");
                    }

                    SubPattern sub = subs[^1];
                    sub.SaveNext = Math.Max(sub.SaveNext, save);
                    save = sub.Save;
                    depth = sub.Depth;
                    sub.Brks.Add(result.Count);
                    result.Add(new Atom(AtomKind.Break, 0));
                    int caseOffset = result.Count - sub.Case - 1;
                    if (caseOffset >= 256)
                    {
                        throw new PatternException("sub pattern too large");
                    }

                    result[sub.Case] = new Atom(AtomKind.Case, (byte)caseOffset);
                    sub.Case = result.Count;
                    result.Add(new Atom(AtomKind.Case, 0));
                    break;
                }
                case (byte)')':
                {
                    if (subs.Count == 0)
                    {
                        throw new PatternException("sub pattern error");
                    }

                    SubPattern sub = subs[^1];
                    subs.RemoveAt(subs.Count - 1);
                    save = Math.Max(sub.SaveNext, save);
                    depth = sub.Depth;
                    result[sub.Case] = new Atom(AtomKind.Nop);
                    foreach (int brk in sub.Brks)
                    {
                        int brkOffset = result.Count - brk - 1;
                        if (brkOffset >= 256)
                        {
                            throw new PatternException("sub pattern too large");
                        }

                        result[brk] = new Atom(AtomKind.Break, (byte)brkOffset);
                    }

                    break;
                }
                case (byte)'[':
                {
                    uint lowerBound = 0;
                    bool atLeastOne = false;
                    byte c;
                    while (true)
                    {
                        if (i >= pat.Length)
                        {
                            throw new PatternException("many invalid syntax");
                        }

                        c = pat[i++];
                        if (c is (byte)'-' or (byte)']')
                        {
                            break;
                        }

                        if (c is >= (byte)'0' and <= (byte)'9')
                        {
                            atLeastOne = true;
                            lowerBound = lowerBound * 10 + (uint)(c - (byte)'0');
                            if (lowerBound >= 16384)
                            {
                                throw new PatternException("many range exceeded");
                            }
                        }
                        else
                        {
                            throw new PatternException("many invalid syntax");
                        }
                    }

                    if (!atLeastOne)
                    {
                        throw new PatternException("many invalid syntax");
                    }

                    if (lowerBound > 0)
                    {
                        if (lowerBound >= 256)
                        {
                            result.Add(new Atom(AtomKind.Rangext, (byte)(lowerBound >> 8)));
                        }

                        result.Add(new Atom(AtomKind.Skip, (byte)(lowerBound & 0xff)));
                    }

                    if (c == (byte)']')
                    {
                        continue;
                    }

                    uint upperBound = 0;
                    while (true)
                    {
                        if (i >= pat.Length)
                        {
                            throw new PatternException("many invalid syntax");
                        }

                        c = pat[i++];
                        if (c == (byte)']')
                        {
                            break;
                        }

                        if (c is >= (byte)'0' and <= (byte)'9')
                        {
                            upperBound = upperBound * 10 + (uint)(c - (byte)'0');
                            if (upperBound >= 16384)
                            {
                                throw new PatternException("many range exceeded");
                            }
                        }
                        else
                        {
                            throw new PatternException("many invalid syntax");
                        }
                    }

                    if (lowerBound < upperBound)
                    {
                        uint manySkip = upperBound - lowerBound;
                        if (manySkip >= 256)
                        {
                            result.Add(new Atom(AtomKind.Rangext, (byte)(manySkip >> 8)));
                        }

                        result.Add(new Atom(AtomKind.Many, (byte)(manySkip & 0xff)));
                    }
                    else
                    {
                        throw new PatternException("many bounds nonsensical");
                    }

                    break;
                }
                case >= (byte)'0' and <= (byte)'9':
                case >= (byte)'A' and <= (byte)'F':
                case >= (byte)'a' and <= (byte)'f':
                {
                    int hi = HexNibble(chr);
                    if (i >= pat.Length)
                    {
                        throw new PatternException("unpaired hex digit");
                    }

                    int lo = HexNibble(pat[i++]);
                    if (lo < 0)
                    {
                        throw new PatternException("unpaired hex digit");
                    }

                    result.Add(new Atom(AtomKind.Byte, (byte)((hi << 4) + lo)));
                    break;
                }
                case (byte)'"':
                    while (true)
                    {
                        if (i >= pat.Length)
                        {
                            throw new PatternException("string missing end quote");
                        }

                        byte c = pat[i++];
                        if (c == (byte)'"')
                        {
                            break;
                        }

                        result.Add(new Atom(AtomKind.Byte, c));
                    }

                    break;
                case (byte)'\'':
                    if (save >= byte.MaxValue)
                    {
                        throw new PatternException("save store overflow");
                    }

                    result.Add(new Atom(AtomKind.Save, (byte)save));
                    save++;
                    break;
                case (byte)'?':
                {
                    Atom last = result[^1];
                    if (last.Kind == AtomKind.Skip && last.Arg != PtrSkip && last.Arg < 255)
                    {
                        result[^1] = new Atom(AtomKind.Skip, (byte)(last.Arg + 1));
                    }
                    else
                    {
                        result.Add(new Atom(AtomKind.Skip, 1));
                    }

                    break;
                }
                case (byte)'@':
                {
                    if (i >= pat.Length)
                    {
                        throw new PatternException("aligned operand error");
                    }

                    byte op = pat[i++];
                    byte value = op switch
                    {
                        >= (byte)'0' and <= (byte)'9' => (byte)(op - (byte)'0'),
                        >= (byte)'A' and <= (byte)'Z' => (byte)(10 + (op - (byte)'A')),
                        >= (byte)'a' and <= (byte)'z' => (byte)(10 + (op - (byte)'a')),
                        _ => throw new PatternException("aligned operand error"),
                    };
                    result.Add(new Atom(AtomKind.Aligned, value));
                    break;
                }
                case (byte)'i':
                    result.Add(ReadOperand(pat, ref i, save, signed: true));
                    if (save >= byte.MaxValue)
                    {
                        throw new PatternException("save store overflow");
                    }

                    save++;
                    break;
                case (byte)'u':
                    result.Add(ReadOperand(pat, ref i, save, signed: false));
                    if (save >= byte.MaxValue)
                    {
                        throw new PatternException("save store overflow");
                    }

                    save++;
                    break;
                case (byte)'z':
                    if (save >= byte.MaxValue)
                    {
                        throw new PatternException("save store overflow");
                    }

                    result.Add(new Atom(AtomKind.Zero, (byte)save));
                    save++;
                    break;
                case (byte)' ':
                case (byte)'\n':
                case (byte)'\r':
                case (byte)'\t':
                    break;
                default:
                    throw new PatternException("unknown character");
            }
        }

        if (depth != 0)
        {
            throw new PatternException("stack unbalanced");
        }

        if (subs.Count != 0)
        {
            throw new PatternException("sub pattern error");
        }

        while (result.Count > 0 && IsRedundant(result[^1].Kind))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result.ToArray();
    }

    /// <summary>Number of save slots a pattern needs (port of pelite's <c>save_len</c>).</summary>
    public static int SaveLen(Atom[] pat)
    {
        int max = 0;
        foreach (Atom atom in pat)
        {
            switch (atom.Kind)
            {
                case AtomKind.Save:
                case AtomKind.Pir:
                case AtomKind.Check:
                case AtomKind.Zero:
                case AtomKind.ReadI8:
                case AtomKind.ReadI16:
                case AtomKind.ReadI32:
                case AtomKind.ReadU8:
                case AtomKind.ReadU16:
                case AtomKind.ReadU32:
                    max = Math.Max(max, atom.Arg + 1);
                    break;
            }
        }

        return max;
    }

    private static Atom ReadOperand(byte[] pat, ref int i, int save, bool signed)
    {
        if (i >= pat.Length)
        {
            throw new PatternException("read operand error");
        }

        byte size = pat[i++];
        return size switch
        {
            (byte)'1' => new Atom(signed ? AtomKind.ReadI8 : AtomKind.ReadU8, (byte)save),
            (byte)'2' => new Atom(signed ? AtomKind.ReadI16 : AtomKind.ReadU16, (byte)save),
            (byte)'4' => new Atom(signed ? AtomKind.ReadI32 : AtomKind.ReadU32, (byte)save),
            _ => throw new PatternException("read operand error"),
        };
    }

    private static int HexNibble(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => c - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - (byte)'A' + 10,
        _ => -1,
    };

    private static bool IsRedundant(AtomKind kind) =>
        kind is AtomKind.Skip or AtomKind.Rangext or AtomKind.Pop or AtomKind.Many;
}

/// <summary>
/// The pattern interpreter + scanner. Ports pelite's pe64 <c>Exec</c> and <c>finds_code</c>:
/// <c>finds_code</c> restricts the scan to <c>BaseOfCode..BaseOfCode+SizeOfCode</c> and only
/// succeeds when the pattern matches at exactly one location (uniqueness guard).
/// </summary>
public sealed class Scanner
{
    private readonly byte[] _image;
    private readonly int _imageLen;
    private readonly uint _baseOfCode;
    private readonly uint _sizeOfCode;

    public Scanner(PeImage image)
    {
        _image = image.Image;
        _imageLen = image.Image.Length;
        _baseOfCode = image.BaseOfCode;
        _sizeOfCode = image.SizeOfCode;
    }

    /// <summary>Runs the interpreter at a fixed cursor over a raw buffer (mirrors pelite's
    /// <c>Scanner::exec</c>); used by the verification harness against pelite's own test vectors.</summary>
    internal static bool ExecForTest(byte[] image, Atom[] pattern, uint cursor, uint[] save)
    {
        var exec = new Exec(image, image.Length, pattern);
        exec.Init(cursor);
        return exec.Run(save);
    }

    public bool FindsCode(Atom[] pattern, uint[] save)
    {
        uint start = _baseOfCode;
        uint end = (uint)Math.Min((ulong)_baseOfCode + _sizeOfCode, (ulong)_imageLen);
        if (start >= end)
        {
            return false;
        }

        var state = new MatchState(_image, _imageLen, pattern, start, end);
        if (!state.Next(save))
        {
            return false;
        }

        // Disallow more than one match: a stale/ambiguous signature must not silently pick one.
        return !state.Next(Array.Empty<uint>());
    }

    private sealed class MatchState
    {
        private readonly Atom[] _pat;
        private readonly Exec _exec;
        private readonly byte[] _image;
        private readonly uint _end;
        private readonly int _qsFirst;
        private uint _start;

        public MatchState(byte[] image, int imageLen, Atom[] pat, uint start, uint end)
        {
            _image = image;
            _pat = pat;
            _start = start;
            _end = end;
            _exec = new Exec(image, imageLen, pat);
            _qsFirst = LeadingByte(pat);
        }

        public bool Next(uint[] save)
        {
            while (_start < _end)
            {
                ReadOnlySpan<byte> region = _image.AsSpan((int)_start, (int)(_end - _start));
                int idx = _qsFirst >= 0 ? region.IndexOf((byte)_qsFirst) : 0;
                if (idx < 0)
                {
                    _start = _end;
                    return false;
                }

                uint pos = _start + (uint)idx;
                _exec.Init(pos);
                bool matched = _exec.Run(save);
                _start = pos + 1;
                if (matched)
                {
                    return true;
                }
            }

            return false;
        }

        private static int LeadingByte(Atom[] pat)
        {
            foreach (Atom atom in pat)
            {
                switch (atom.Kind)
                {
                    case AtomKind.Byte:
                        return atom.Arg;
                    case AtomKind.Save:
                    case AtomKind.Aligned:
                    case AtomKind.Nop:
                        continue;
                    default:
                        return -1;
                }
            }

            return -1;
        }
    }

    /// <summary>Recursive pattern interpreter, reused across candidates (no per-candidate alloc).</summary>
    private sealed class Exec
    {
        private const uint SkipVa = 8; // sizeof(Va) on PE64

        private readonly byte[] _image;
        private readonly int _len;
        private readonly Atom[] _pat;
        private uint _cursor;
        private int _pc;

        public Exec(byte[] image, int len, Atom[] pat)
        {
            _image = image;
            _len = len;
            _pat = pat;
        }

        public void Init(uint cursor)
        {
            _cursor = cursor;
            _pc = 0;
        }

        public bool Run(uint[] save)
        {
            int mask = 0xff;
            uint extRange = 0;

            while (_pc < _pat.Length)
            {
                Atom atom = _pat[_pc];
                _pc++;
                switch (atom.Kind)
                {
                    case AtomKind.Byte:
                        if (!TryU8(_cursor, out byte b) || (b & mask) != (atom.Arg & mask))
                        {
                            return false;
                        }

                        mask = 0xff;
                        _cursor = unchecked(_cursor + 1);
                        break;
                    case AtomKind.Save:
                        if (atom.Arg < save.Length)
                        {
                            save[atom.Arg] = _cursor;
                        }

                        break;
                    case AtomKind.Push:
                    {
                        uint skip = extRange + atom.Arg;
                        if (skip == 0)
                        {
                            skip = SkipVa;
                        }

                        uint ret = unchecked(_cursor + skip);
                        if (!Run(save))
                        {
                            return false;
                        }

                        mask = 0xff;
                        extRange = 0;
                        _cursor = ret;
                        break;
                    }
                    case AtomKind.Pop:
                        return true;
                    case AtomKind.Fuzzy:
                        mask = atom.Arg;
                        break;
                    case AtomKind.Skip:
                    {
                        uint skip = extRange + atom.Arg;
                        if (skip == 0)
                        {
                            skip = SkipVa;
                        }

                        _cursor = unchecked(_cursor + skip);
                        extRange = 0;
                        break;
                    }
                    case AtomKind.Back:
                    {
                        uint back = extRange + atom.Arg;
                        if (back == 0)
                        {
                            back = SkipVa;
                        }

                        _cursor = unchecked(_cursor - back);
                        extRange = 0;
                        break;
                    }
                    case AtomKind.Rangext:
                        extRange = (uint)atom.Arg * 256;
                        break;
                    case AtomKind.Many:
                        return RunMany(save, extRange + atom.Arg);
                    case AtomKind.Jump1:
                        if (!TryI8(_cursor, out sbyte sb))
                        {
                            return false;
                        }

                        _cursor = unchecked(_cursor + (uint)(int)sb + 1);
                        break;
                    case AtomKind.Jump4:
                        if (!TryI32(_cursor, out int sd))
                        {
                            return false;
                        }

                        _cursor = unchecked(_cursor + (uint)sd + 4);
                        break;
                    case AtomKind.Ptr:
                        if (!TryU64(_cursor, out ulong va))
                        {
                            return false;
                        }

                        // Absolute pointer follow: only used by `*`; unused by cs2 patterns.
                        // Translate VA -> RVA assuming a section-aligned image at offset 0.
                        _cursor = unchecked((uint)va);
                        break;
                    case AtomKind.Pir:
                    {
                        if (!TryI32(_cursor, out int pd))
                        {
                            return false;
                        }

                        uint baseRva = atom.Arg < save.Length ? save[atom.Arg] : _cursor;
                        _cursor = unchecked(baseRva + (uint)pd);
                        break;
                    }
                    case AtomKind.VTypeName:
                        return false; // unused by cs2 patterns
                    case AtomKind.Check:
                        if (atom.Arg < save.Length && save[atom.Arg] != _cursor)
                        {
                            return false;
                        }

                        break;
                    case AtomKind.Aligned:
                        if (atom.Arg < 32 && (_cursor & ((1u << atom.Arg) - 1)) != 0)
                        {
                            return false;
                        }

                        break;
                    case AtomKind.ReadU8:
                        if (!TryU8(_cursor, out byte u8))
                        {
                            return false;
                        }

                        Store(save, atom.Arg, u8);
                        _cursor = unchecked(_cursor + 1);
                        break;
                    case AtomKind.ReadI8:
                        if (!TryI8(_cursor, out sbyte i8))
                        {
                            return false;
                        }

                        Store(save, atom.Arg, unchecked((uint)(int)i8));
                        _cursor = unchecked(_cursor + 1);
                        break;
                    case AtomKind.ReadU16:
                        if (!TryU16(_cursor, out ushort u16))
                        {
                            return false;
                        }

                        Store(save, atom.Arg, u16);
                        _cursor = unchecked(_cursor + 2);
                        break;
                    case AtomKind.ReadI16:
                        if (!TryU16(_cursor, out ushort s16))
                        {
                            return false;
                        }

                        Store(save, atom.Arg, unchecked((uint)(int)(short)s16));
                        _cursor = unchecked(_cursor + 2);
                        break;
                    case AtomKind.ReadU32:
                    case AtomKind.ReadI32:
                        if (!TryU32(_cursor, out uint dword))
                        {
                            return false;
                        }

                        Store(save, atom.Arg, dword);
                        _cursor = unchecked(_cursor + 4);
                        break;
                    case AtomKind.Zero:
                        Store(save, atom.Arg, 0);
                        break;
                    case AtomKind.Case:
                    {
                        int pc0 = _pc;
                        uint cursor0 = _cursor;
                        if (!Run(save))
                        {
                            _pc = pc0 + atom.Arg;
                            _cursor = cursor0;
                        }

                        break;
                    }
                    case AtomKind.Break:
                        _pc += atom.Arg;
                        return true;
                    case AtomKind.Nop:
                        break;
                }
            }

            return true;
        }

        private bool RunMany(uint[] save, uint limit)
        {
            uint cursor0 = _cursor;
            int pc0 = _pc;

            if (cursor0 > (uint)_len)
            {
                return false;
            }

            int avail = _len - (int)cursor0;
            int bytesLen = limit == 0 ? avail : Math.Min((int)limit, avail);

            int peek = -1;
            for (int k = pc0; k < _pat.Length; k++)
            {
                if (_pat[k].Kind == AtomKind.Byte)
                {
                    peek = _pat[k].Arg;
                    break;
                }

                if (_pat[k].Kind == AtomKind.Save)
                {
                    continue;
                }

                break;
            }

            for (int i = 0; i < bytesLen; i++)
            {
                if (peek >= 0 && _image[(int)cursor0 + i] != peek)
                {
                    continue;
                }

                _cursor = cursor0 + (uint)i;
                _pc = pc0;
                if (Run(save))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Store(uint[] save, byte slot, uint value)
        {
            if (slot < save.Length)
            {
                save[slot] = value;
            }
        }

        private bool TryU8(uint rva, out byte value)
        {
            if ((ulong)rva + 1 <= (ulong)_len)
            {
                value = _image[(int)rva];
                return true;
            }

            value = 0;
            return false;
        }

        private bool TryI8(uint rva, out sbyte value)
        {
            if (TryU8(rva, out byte b))
            {
                value = unchecked((sbyte)b);
                return true;
            }

            value = 0;
            return false;
        }

        private bool TryU16(uint rva, out ushort value)
        {
            if ((ulong)rva + 2 <= (ulong)_len)
            {
                value = BinaryPrimitives.ReadUInt16LittleEndian(_image.AsSpan((int)rva));
                return true;
            }

            value = 0;
            return false;
        }

        private bool TryI32(uint rva, out int value)
        {
            if (TryU32(rva, out uint u))
            {
                value = unchecked((int)u);
                return true;
            }

            value = 0;
            return false;
        }

        private bool TryU32(uint rva, out uint value)
        {
            if ((ulong)rva + 4 <= (ulong)_len)
            {
                value = BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan((int)rva));
                return true;
            }

            value = 0;
            return false;
        }

        private bool TryU64(uint rva, out ulong value)
        {
            if ((ulong)rva + 8 <= (ulong)_len)
            {
                value = BinaryPrimitives.ReadUInt64LittleEndian(_image.AsSpan((int)rva));
                return true;
            }

            value = 0;
            return false;
        }
    }
}
