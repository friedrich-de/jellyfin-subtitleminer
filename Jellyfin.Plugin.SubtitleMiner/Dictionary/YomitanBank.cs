using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.SubtitleMiner.Dictionary;

/// <summary>The words of one term bank, as <see cref="YomitanBank.ReadTerms"/> read them.</summary>
internal sealed class TermBank
{
    private readonly ByteBuilder _rows;

    /// <summary>Initializes a new instance of the <see cref="TermBank"/> class.</summary>
    /// <param name="rows">The words.</param>
    /// <param name="blocks">Their definitions.</param>
    /// <param name="count">How many words.</param>
    /// <param name="skipped">How many rows could not be read.</param>
    public TermBank(ByteBuilder rows, IReadOnlyList<byte[]> blocks, int count, int skipped)
    {
        _rows = rows;
        Blocks = blocks;
        Count = count;
        Skipped = skipped;
    }

    /// <summary>
    /// Gets the words one after another: how it is written, how it is read, its definition tags, its rules and its
    /// own tags, each as a length and that many bytes of UTF-8; then its score and its sequence (8 bytes each) and
    /// where its definitions are: the block's place in <see cref="Blocks"/>, the start in the block and the length
    /// (4 bytes each). Numbers are little-endian.
    /// </summary>
    public ReadOnlyMemory<byte> Rows => _rows.Memory;

    /// <summary>Gets the definitions, in blocks as <see cref="DictionaryDatabase.Pack"/> keeps them.</summary>
    public IReadOnlyList<byte[]> Blocks { get; }

    /// <summary>Gets how many words there are.</summary>
    public int Count { get; }

    /// <summary>Gets how many rows could not be read.</summary>
    public int Skipped { get; }

    /// <summary>Gives the memory of <see cref="Rows"/> back, once they are in the file.</summary>
    public void Release() => _rows.Release();
}

/// <summary>The frequencies, pitch accents and pronunciations of one bank, as <see cref="YomitanBank.ReadMetas"/> read them.</summary>
internal sealed class MetaBank
{
    private readonly ByteBuilder _rows;

    /// <summary>Initializes a new instance of the <see cref="MetaBank"/> class.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="frequencies">How many frequencies.</param>
    /// <param name="pitches">How many pitch accents and pronunciations.</param>
    /// <param name="skipped">How many rows could not be read.</param>
    public MetaBank(ByteBuilder rows, int frequencies, int pitches, int skipped)
    {
        _rows = rows;
        Frequencies = frequencies;
        Pitches = pitches;
        Skipped = skipped;
    }

    /// <summary>
    /// Gets the rows one after another: the word as a length and that many bytes of UTF-8, one byte for what the
    /// row is (<see cref="YomitanBank.FrequencyMode"/> and the like), and what it says as a length and that many
    /// bytes of JSON.
    /// </summary>
    public ReadOnlyMemory<byte> Rows => _rows.Memory;

    /// <summary>Gets how many frequencies there are.</summary>
    public int Frequencies { get; }

    /// <summary>Gets how many pitch accents and pronunciations there are.</summary>
    public int Pitches { get; }

    /// <summary>Gets how many rows could not be read.</summary>
    public int Skipped { get; }

    /// <summary>Gives the memory of <see cref="Rows"/> back, once they are in the file.</summary>
    public void Release() => _rows.Release();
}

/// <summary>
/// Reads the banks of a dictionary in Yomitan's format. A bank is read in one pass over its bytes, without
/// building it up as a tree: a word's definitions are by far most of a bank, and they are taken as the bytes they
/// are written in, neither read nor written anew. That is what makes an import take seconds, and it is how
/// Hoshidicts, the engine of Hoshi Reader and Hachidori, reads them too.
/// </summary>
internal static class YomitanBank
{
    /// <summary>A row of a meta bank that is a frequency.</summary>
    internal const byte FrequencyMode = 0;

    /// <summary>A row of a meta bank that is a pitch accent.</summary>
    internal const byte PitchMode = 1;

    /// <summary>A row of a meta bank that is a pronunciation.</summary>
    internal const byte PronunciationMode = 2;

    private static readonly JsonReaderOptions Json = new() { MaxDepth = 1024 };

    /// <summary>Reads a term bank.</summary>
    /// <param name="json">The bank.</param>
    /// <param name="format">The format the dictionary is written in: 1 lists a word's definitions after its score, later ones have them as a list of their own.</param>
    /// <param name="name">The bank's name in the dictionary, for what an error says.</param>
    /// <returns>The words.</returns>
    /// <exception cref="InvalidDataException">The bank is not a list of rows in JSON.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static TermBank ReadTerms(ReadOnlySpan<byte> json, int format, string name)
    {
        json = WithoutMark(json);
        var rows = new ByteBuilder(Math.Max(256, json.Length / 6));
        var definitions = new Definitions();
        var count = 0;
        var skipped = 0;
        try
        {
            var reader = new Utf8JsonReader(json, Json);
            Open(ref reader, name);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    skipped++;
                }
                else if (ReadTerm(ref reader, json, format, rows, definitions))
                {
                    count++;
                }
                else
                {
                    skipped++;
                }
            }

            // Reads to the end: anything after the list makes the bank no JSON.
            reader.Read();
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"{name} in that dictionary is not JSON.");
        }

        definitions.Finish();
        return new TermBank(rows, definitions.Blocks, count, skipped);
    }

    /// <summary>Reads a term meta bank: frequencies, pitch accents and pronunciations.</summary>
    /// <param name="json">The bank.</param>
    /// <param name="name">The bank's name in the dictionary, for what an error says.</param>
    /// <returns>The rows.</returns>
    /// <exception cref="InvalidDataException">The bank is not a list of rows in JSON.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static MetaBank ReadMetas(ReadOnlySpan<byte> json, string name)
    {
        json = WithoutMark(json);
        var rows = new ByteBuilder(Math.Max(256, json.Length));
        var frequencies = 0;
        var pitches = 0;
        var skipped = 0;
        try
        {
            var reader = new Utf8JsonReader(json, Json);
            Open(ref reader, name);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    skipped++;
                    continue;
                }

                switch (ReadMeta(ref reader, json, rows))
                {
                    case FrequencyMode:
                        frequencies++;
                        break;
                    case PitchMode or PronunciationMode:
                        pitches++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }

            reader.Read();
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"{name} in that dictionary is not JSON.");
        }

        return new MetaBank(rows, frequencies, pitches, skipped);
    }

    /// <summary>Takes the next text off rows that <see cref="ReadTerms"/> or <see cref="ReadMetas"/> wrote.</summary>
    /// <param name="rows">The rows, from where the text's length is; afterwards, from behind the text.</param>
    /// <returns>The text.</returns>
    internal static ReadOnlySpan<byte> Take(ref ReadOnlySpan<byte> rows)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(rows);
        var text = rows.Slice(4, length);
        rows = rows[(4 + length)..];
        return text;
    }

    /// <summary>
    /// Reads a row: [expression, reading, definitionTags, rules, score, glossary[], sequence, termTags], or in
    /// format 1 [expression, reading, definitionTags, rules, score, ...glossary strings].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool ReadTerm(ref Utf8JsonReader reader, ReadOnlySpan<byte> json, int format, ByteBuilder rows, Definitions definitions)
    {
        var rowAt = rows.Length;
        var listed = format == 1;
        var good = true;
        var index = 0;
        var expressionAt = 0;
        var expressionLength = 0;
        var score = 0d;
        var sequence = 0L;
        var glossaryAt = -1;
        var glossaryEnd = -1;
        var tagged = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var token = reader.TokenType;
            var at = (int)reader.TokenStartIndex;
            if (good)
            {
                switch (index)
                {
                    case 0:
                        good = token == JsonTokenType.String && WriteText(ref reader, rows, out expressionAt, out expressionLength)
                            && IsKey(rows.Written.Slice(expressionAt, expressionLength));
                        break;
                    case 1:
                        if (token != JsonTokenType.String || !WriteText(ref reader, rows, out var readingAt, out var readingLength))
                        {
                            good = false;
                        }
                        else if (readingLength == 0)
                        {
                            // A word without a reading is read as it is written.
                            rows.Truncate(readingAt - 4);
                            rows.WriteInt32(expressionLength);
                            rows.Reserve(expressionLength);
                            rows.Written.Slice(expressionAt, expressionLength).CopyTo(rows.Written[^expressionLength..]);
                        }
                        else
                        {
                            good = IsKey(rows.Written.Slice(readingAt, readingLength));
                        }

                        break;
                    case 2 or 3:
                        WriteTags(ref reader, token, rows);
                        break;
                    case 4:
                        good = Number(ref reader, token, out score);
                        break;
                    case 5:
                        good = listed || token == JsonTokenType.StartArray;
                        glossaryAt = at;
                        break;
                    case 6:
                        if (!listed && Number(ref reader, token, out var number))
                        {
                            sequence = (long)Math.Round(number);
                        }

                        break;
                    case 7:
                        if (!listed)
                        {
                            WriteTags(ref reader, token, rows);
                            tagged = true;
                        }

                        break;
                }
            }

            if (token is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                reader.Skip();
            }

            if (index == 5 || (listed && index > 5))
            {
                glossaryEnd = (int)reader.BytesConsumed;
            }

            index++;
        }

        if (!good || index < (listed ? 5 : 6))
        {
            rows.Truncate(rowAt);
            return false;
        }

        if (!tagged)
        {
            rows.WriteInt32(0);
        }

        var (block, start, length) = listed
            ? definitions.AddListed(glossaryAt < 0 ? default : json[glossaryAt..glossaryEnd])
            : definitions.Add(json, glossaryAt, glossaryEnd);
        rows.WriteInt64((long)Math.Round(score));
        rows.WriteInt64(sequence);
        rows.WriteInt32(block);
        rows.WriteInt32(start);
        rows.WriteInt32(length);
        return true;
    }

    /// <summary>Reads a row: [expression, "freq" | "pitch" | "ipa", data]. Gives what the row is, or 255 for one that cannot be read.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static byte ReadMeta(ref Utf8JsonReader reader, ReadOnlySpan<byte> json, ByteBuilder rows)
    {
        const byte Unread = 255;
        var rowAt = rows.Length;
        var good = true;
        var index = 0;
        var mode = Unread;
        var dataAt = 0;
        var dataEnd = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var token = reader.TokenType;
            var at = (int)reader.TokenStartIndex;
            if (good)
            {
                switch (index)
                {
                    case 0:
                        good = token == JsonTokenType.String && WriteText(ref reader, rows, out var expressionAt, out var expressionLength)
                            && IsKey(rows.Written.Slice(expressionAt, expressionLength));
                        break;
                    case 1:
                        mode = token != JsonTokenType.String ? Unread
                            : reader.ValueTextEquals("freq"u8) ? FrequencyMode
                            : reader.ValueTextEquals("pitch"u8) ? PitchMode
                            : reader.ValueTextEquals("ipa"u8) ? PronunciationMode
                            : Unread;
                        good = mode != Unread;
                        break;
                    case 2:
                        good = token != JsonTokenType.Null;
                        dataAt = at;
                        break;
                }
            }

            if (token is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                reader.Skip();
            }

            if (index == 2)
            {
                dataEnd = (int)reader.BytesConsumed;
            }

            index++;
        }

        if (!good || index < 3)
        {
            rows.Truncate(rowAt);
            return Unread;
        }

        rows.WriteByte(mode);
        rows.WriteInt32(dataEnd - dataAt);
        rows.Write(json[dataAt..dataEnd]);
        return mode;
    }

    /// <summary>Steps into the list of rows a bank is.</summary>
    private static void Open(ref Utf8JsonReader reader, string name)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            throw new InvalidDataException($"{name} in that dictionary is not a list of rows.");
        }
    }

    /// <summary>Some tools write a byte order mark first, which JSON does not have.</summary>
    private static ReadOnlySpan<byte> WithoutMark(ReadOnlySpan<byte> json) =>
        json.StartsWith("﻿"u8) ? json[3..] : json;

    /// <summary>Reads a number that fits a long, or says it is none.</summary>
    private static bool Number(ref Utf8JsonReader reader, JsonTokenType token, out double number)
    {
        number = 0;
        return token == JsonTokenType.Number && reader.TryGetDouble(out number) && YomitanArchive.FitsLong(number);
    }

    /// <summary>A word as it is written or read is looked up by: not empty, and not longer than a word is.</summary>
    private static bool IsKey(ReadOnlySpan<byte> utf8) =>
        utf8.Length > 0 && (utf8.Length <= YomitanArchive.MaxKeyLength || Encoding.UTF8.GetCharCount(utf8) <= YomitanArchive.MaxKeyLength);

    /// <summary>
    /// Writes the string the reader is at as its length and its text, in UTF-8 with its escapes undone. Says
    /// <c>false</c>, and writes nothing, for a string that is no text (half of a surrogate pair, say).
    /// </summary>
    private static bool WriteText(ref Utf8JsonReader reader, ByteBuilder to, out int at, out int length)
    {
        var lengthAt = to.Length;
        to.WriteInt32(0);
        at = to.Length;
        var value = reader.ValueSpan;
        if (!reader.ValueIsEscaped)
        {
            to.Write(value);
        }
        else
        {
            // Text is never longer with its escapes undone.
            var room = to.Reserve(value.Length);
            int written;
            try
            {
                written = reader.CopyString(room);
            }
            catch (InvalidOperationException)
            {
                to.Truncate(lengthAt);
                length = 0;
                return false;
            }

            to.Truncate(at + written);
        }

        length = to.Length - at;
        BinaryPrimitives.WriteInt32LittleEndian(to.Written[lengthAt..], length);
        return true;
    }

    /// <summary>Writes tags: the string the reader is at without the white space around it, or nothing for anything but a string.</summary>
    private static void WriteTags(ref Utf8JsonReader reader, JsonTokenType token, ByteBuilder to)
    {
        if (token != JsonTokenType.String || !WriteText(ref reader, to, out var at, out var length))
        {
            to.WriteInt32(0);
            return;
        }

        var text = to.Written.Slice(at, length);
        var trimmed = Trim(text);
        if (trimmed.Length != length)
        {
            trimmed.CopyTo(text);
            to.Truncate(at + trimmed.Length);
            BinaryPrimitives.WriteInt32LittleEndian(to.Written[(at - 4)..], trimmed.Length);
        }
    }

    /// <summary>Text without the white space at its ends, as <see cref="string.Trim()"/> takes it off.</summary>
    private static Span<byte> Trim(Span<byte> utf8)
    {
        while (utf8.Length > 0 && Rune.DecodeFromUtf8(utf8, out var first, out var taken) == OperationStatus.Done && Rune.IsWhiteSpace(first))
        {
            utf8 = utf8[taken..];
        }

        while (utf8.Length > 0 && Rune.DecodeLastFromUtf8(utf8, out var last, out var taken) == OperationStatus.Done && Rune.IsWhiteSpace(last))
        {
            utf8 = utf8[..^taken];
        }

        return utf8;
    }

    /// <summary>
    /// Gathers the definitions of a bank's words into blocks, and compresses each block once it is full.
    /// </summary>
    private sealed class Definitions
    {
        private readonly ByteBuilder _block = new(DictionaryDatabase.BlockBytes);
        private readonly List<byte[]> _blocks = [];

        // The definitions of the word before: where the bank has them, and where they went.
        private int _lastAt = -1;
        private int _lastEnd = -1;
        private (int Block, int Start, int Length) _last;

        /// <summary>Gets the blocks that are full.</summary>
        public IReadOnlyList<byte[]> Blocks => _blocks;

        /// <summary>
        /// Adds the definitions the bank has from <paramref name="at"/> to <paramref name="end"/>. A dictionary
        /// often has one entry as several rows, one for each way to write the word, all with the same definitions
        /// (a fifth of Jitendex's rows): such a row points at the definitions of the row before.
        /// </summary>
        public (int Block, int Start, int Length) Add(ReadOnlySpan<byte> json, int at, int end)
        {
            var definitions = json[at..end];
            if (_lastAt < 0 || !definitions.SequenceEqual(json[_lastAt.._lastEnd]))
            {
                _last = Put(definitions, default, default);
            }

            _lastAt = at;
            _lastEnd = end;
            return _last;
        }

        /// <summary>Adds the definitions of a row of format 1, which lists them: they become a list.</summary>
        public (int Block, int Start, int Length) AddListed(ReadOnlySpan<byte> items) => Put(items, "["u8, "]"u8);

        /// <summary>Compresses the block that is being filled, if anything is in it.</summary>
        public void Flush()
        {
            if (_block.Length > 0)
            {
                _blocks.Add(DictionaryDatabase.Pack(_block.Written));
                _block.Truncate(0);
            }
        }

        /// <summary>Compresses the last block. Nothing may be added afterwards.</summary>
        public void Finish()
        {
            Flush();
            _block.Release();
        }

        private (int Block, int Start, int Length) Put(ReadOnlySpan<byte> definitions, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
        {
            // A block is over its size only when one word's definitions are.
            var length = before.Length + definitions.Length + after.Length;
            if (_block.Length > 0 && _block.Length + length > DictionaryDatabase.BlockBytes)
            {
                Flush();
            }

            var start = _block.Length;
            _block.Write(before);
            _block.Write(definitions);
            _block.Write(after);
            return (_blocks.Count, start, length);
        }
    }
}

/// <summary>
/// Bytes written one after another into an array that grows. The array is borrowed: an import fills and empties
/// hundreds of these, each of some hundred kilobytes, which the garbage collector would otherwise have to clear
/// away one by one.
/// </summary>
internal sealed class ByteBuilder
{
    private byte[] _bytes;

    /// <summary>Initializes a new instance of the <see cref="ByteBuilder"/> class.</summary>
    /// <param name="capacity">How many bytes it has room for at first.</param>
    public ByteBuilder(int capacity)
    {
        _bytes = ArrayPool<byte>.Shared.Rent(Math.Max(16, capacity));
    }

    /// <summary>Gets how many bytes are written.</summary>
    public int Length { get; private set; }

    /// <summary>Gets what is written.</summary>
    public Span<byte> Written => _bytes.AsSpan(0, Length);

    /// <summary>Gets what is written, to keep until <see cref="Release"/>.</summary>
    public ReadOnlyMemory<byte> Memory => _bytes.AsMemory(0, Length);

    /// <summary>Makes room for more bytes at the end, which count as written.</summary>
    /// <param name="count">How many.</param>
    /// <returns>The room.</returns>
    public Span<byte> Reserve(int count)
    {
        if (_bytes.Length - Length < count)
        {
            var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(Array.MaxLength, Math.Max((long)_bytes.Length * 2, (long)Length + count)));
            Written.CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = larger;
        }

        Length += count;
        return _bytes.AsSpan(Length - count, count);
    }

    /// <summary>Writes bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    public void Write(ReadOnlySpan<byte> bytes) => bytes.CopyTo(Reserve(bytes.Length));

    /// <summary>Writes one byte.</summary>
    /// <param name="value">The byte.</param>
    public void WriteByte(byte value) => Reserve(1)[0] = value;

    /// <summary>Writes a number as 4 bytes.</summary>
    /// <param name="value">The number.</param>
    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);

    /// <summary>Writes a number as 8 bytes.</summary>
    /// <param name="value">The number.</param>
    public void WriteInt64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);

    /// <summary>Forgets what was written after the first <paramref name="length"/> bytes.</summary>
    /// <param name="length">How many bytes stay.</param>
    public void Truncate(int length) => Length = length;

    /// <summary>Gives the array back. Nothing of this may be used afterwards.</summary>
    public void Release()
    {
        if (_bytes.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = [];
            Length = 0;
        }
    }
}
