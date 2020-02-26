﻿using K4os.Compression.LZ4;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OpenVIII
{
    public class ArchiveMap : IReadOnlyDictionary<string, FI>
    {
        #region Fields

        private Dictionary<string, FI> entries;

        #endregion Fields

        #region Constructors

        public ArchiveMap(byte[] fi, byte[] fl)
        {
            FL list = new FL(fl);
            using (BinaryReader br = new BinaryReader(new MemoryStream(fi, false)))
                entries = list.ToDictionary(x => x, x => Extended.ByteArrayToClass<FI>(br.ReadBytes(12)));
            CorrectCompressionforLZSfiles();
        }

        public ArchiveMap(StreamWithRangeValues fI, StreamWithRangeValues fL)
        {
            if (fI == null || fL == null)
                throw new ArgumentNullException($"{nameof(ArchiveMap)}::{nameof(ArchiveMap)} {nameof(fI)} or {nameof(fL)} is null");

            Stream s1 = Uncompress(fL, out long flOffset);
            Stream s2 = Uncompress(fI, out long fiOffset);
            long fiSize = fI.UncompressedSize == 0 ? fI.Size : fI.UncompressedSize;

            using (StreamReader sr = new StreamReader(s1, System.Text.Encoding.UTF8))
            using (BinaryReader br = new BinaryReader(s2))
            {
                s1.Seek(flOffset, SeekOrigin.Begin);
                s2.Seek(fiOffset, SeekOrigin.Begin);
                entries = new Dictionary<string, FI>();
                long Count = fiSize / 12;
                while (Count-- > 0)
                    entries.Add(sr.ReadLine().TrimEnd(), Extended.ByteArrayToClass<FI>(br.ReadBytes(12)));
            }
            CorrectCompressionforLZSfiles();
        }

        public ArchiveMap(int count) => entries = new Dictionary<string, FI>(count);

        #endregion Constructors

        #region Properties

        public int Count => ((IReadOnlyDictionary<string, FI>)entries).Count;

        public IEnumerable<string> Keys => ((IReadOnlyDictionary<string, FI>)FilteredEntries).Keys;

        public IReadOnlyList<KeyValuePair<string, FI>> OrderedByName => FilteredEntries.OrderBy(x => x.Key).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

        public IReadOnlyList<KeyValuePair<string, FI>> OrderedByOffset => FilteredEntries.OrderBy(x => x.Value.Offset).ThenBy(x => x.Key).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

        public IEnumerable<FI> Values => ((IReadOnlyDictionary<string, FI>)FilteredEntries).Values;

        private IEnumerable<KeyValuePair<string, FI>> FilteredEntries => entries.Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value.UncompressedSize > 0);

        #endregion Properties

        #region Indexers

        public FI this[string key] => ((IReadOnlyDictionary<string, FI>)entries)[key];

        #endregion Indexers

        #region Methods

        public static byte[] LZ4Uncompress(byte[] input, int fsUncompressedSize, int offset = 12)
        {
            Memory.Log.WriteLine($"{nameof(ArchiveMap)}::{nameof(LZ4Uncompress)} :: decompressing data");
            //ReadOnlySpan<byte> input = new ReadOnlySpan<byte>(file);
            byte[] output = new byte[fsUncompressedSize];
            //Span<byte> output = new Span<byte>(r);
            int count = 0;
            while (input.Length - offset > 0 && (count = LZ4Codec.Decode(input, offset, input.Length - offset, output, 0, output.Length)) <= -1)
            {
                offset++;
            }
            if (offset > -1)
            {
                Memory.Log.WriteLine($"{nameof(ArchiveWorker)}::{nameof(LZ4Uncompress)}::{nameof(offset)}: {offset}");
                return output;
            }
            else
                throw new Exception($"{nameof(ArchiveWorker)}::{nameof(LZ4Uncompress)} Failed to uncompress...");
        }

        public void Add(KeyValuePair<string, FI> keyValuePair) => entries.Add(keyValuePair.Key, keyValuePair.Value);

        public bool ContainsKey(string key) => ((IReadOnlyDictionary<string, FI>)entries).ContainsKey(key);

        public FI FindString(ref string input, out int size)
        {
            string _input = input;
            KeyValuePair<string, FI> result = OrderedByName.FirstOrDefault(x => x.Key.IndexOf(_input, StringComparison.OrdinalIgnoreCase) > -1);
            if (string.IsNullOrWhiteSpace(result.Key))
            {
                size = 0;
                return null;
            }
            KeyValuePair<string, FI> result2 = OrderedByOffset.FirstOrDefault(x => x.Value.Offset > result.Value.Offset);
            if (result2.Value == default && result2.Value == default)
                size = 0;
            else
                size = result2.Value.Offset - result.Value.Offset;
            input = result.Key;
            return result.Value;
        }

        public byte[] GetBinaryFile(FI fi, Stream data, string input, int size, long offset = 0)
        {
            long max = data.Length;
            if (fi == null)
            {
                Memory.Log.WriteLine($"{nameof(ArchiveMap)}::{nameof(GetBinaryFile)} failed to extract {input}");
                return null;
            }
            else
                Memory.Log.WriteLine($"{nameof(ArchiveMap)}::{nameof(GetBinaryFile)} extracting {input}");
            if (size == 0)
                size = checked((int)(max - (fi.Offset + offset)));
            byte[] buffer;
            using (BinaryReader br = new BinaryReader(data))
            {
                br.BaseStream.Seek(fi.Offset + offset, SeekOrigin.Begin);
                switch (fi.CompressionType)
                {
                    case CompressionType.LZSS:
                    case CompressionType.LZSS_UnknownSize:
                    case CompressionType.LZSS_LZSS:
                        int readSize = br.ReadInt32();
                        if (size != readSize + sizeof(int))
                            throw new InvalidDataException($"{nameof(ArchiveMap)}::{nameof(GetBinaryFile)} Size inside of lzs {{{readSize}}} doesn't match size calculated by region {{{size}}}.");
                        size = readSize;
                        break;
                }
                buffer = br.ReadBytes(size);
            }
            switch (fi.CompressionType)
            {
                case 0:
                    return buffer;

                case CompressionType.LZSS:
                    return LZSS.DecompressAllNew(buffer, fi.UncompressedSize);

                case CompressionType.LZSS_LZSS:
                    return LZSS.DecompressAllNew(LZSS.DecompressAllNew(buffer, fi.UncompressedSize), 0, true);

                case CompressionType.LZSS_UnknownSize:
                    return LZSS.DecompressAllNew(buffer, 0);

                case CompressionType.LZ4:
                    return LZ4Uncompress(buffer, fi.UncompressedSize);

                default:
                    throw new InvalidDataException($"{nameof(fi.CompressionType)}: {fi.CompressionType} is invalid...");
            }
        }

        public byte[] GetBinaryFile(string input, Stream data)
        {
            if (data == null)
                throw new ArgumentNullException($"{nameof(ArchiveMap)}::{nameof(GetBinaryFile)} {nameof(data)} cannot be null.");
            if (string.IsNullOrWhiteSpace(input))
                throw new ArgumentNullException($"{nameof(ArchiveMap)}::{nameof(GetBinaryFile)} {nameof(input)} cannot be empty or null");
            long offset = 0;
            if (data.GetType() == typeof(StreamWithRangeValues))
            {
                StreamWithRangeValues s = (StreamWithRangeValues)data;

                data = Uncompress(s, out offset);
                //do I need to do something here? :P
            }
            FI fi = FindString(ref input, out int size);
            return GetBinaryFile(fi, data, input, size, offset);
        }

        public IEnumerator<KeyValuePair<string, FI>> GetEnumerator() => ((IReadOnlyDictionary<string, FI>)entries).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => ((IReadOnlyDictionary<string, FI>)entries).GetEnumerator();

        public KeyValuePair<string, FI> GetFileData(string fileName)
        {
            if (!TryGetValue(fileName, out FI value))
                return OrderedByName.FirstOrDefault(x => x.Key.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0);
            return new KeyValuePair<string, FI>(fileName, value);
        }

        /// <summary>
        /// Merge two file lists together.
        /// </summary>
        /// <param name="child">the map being merged.</param>
        /// <param name="offset_for_fs">offset where the file data is.</param>
        public void MergeMaps(ArchiveMap child, int offset_for_fs) => entries.AddRange(child.entries.ToDictionary(x => x.Key, x => x.Value.Adjust(offset_for_fs)));

        public bool TryGetValue(string key, out FI value) => ((IReadOnlyDictionary<string, FI>)entries).TryGetValue(key, out value);

        /// <summary>
        /// This forces the compression to be set to lzss so the file comes out uncompressed.
        /// </summary>
        private void CorrectCompressionforLZSfiles()
        {
            IEnumerable<KeyValuePair<string, FI>> lszfiles = entries.Where(x => x.Key.EndsWith("lzs", StringComparison.OrdinalIgnoreCase));
            lszfiles.Where(x => x.Value.CompressionType == CompressionType.None).ForEach(x => entries[x.Key].CompressionType = CompressionType.LZSS_UnknownSize);
            lszfiles.Where(x => x.Value.CompressionType == CompressionType.LZSS).ForEach(x => entries[x.Key].CompressionType = CompressionType.LZSS_LZSS);
        }

        private Stream Uncompress(StreamWithRangeValues @in, out long offset)
        {
            offset = 0;
            if (@in == null) return null;
            byte[] buffer = null;
            byte[] open(int skip = 0)
            {
                @in.Seek(@in.Offset + skip, SeekOrigin.Begin);
                using (BinaryReader br = new BinaryReader(@in))
                    return br.ReadBytes(checked((int)(@in.Size - skip)));
            }
            if (@in.Compression > 0)
                switch (@in.Compression)
                {
                    case CompressionType.LZSS:
                        buffer = open(0);
                        int compsize = BitConverter.ToInt32(buffer, 0);
                        if (compsize != buffer.Length - sizeof(int))
                            throw new InvalidDataException($"{nameof(ArchiveMap)}::{nameof(Uncompress)} buffer size incorrect ({compsize}) != ({buffer.Length - sizeof(int)})");
                        return new MemoryStream(LZSS.DecompressAllNew(buffer, @in.UncompressedSize, true));

                    case CompressionType.LZ4:
                        buffer = open();
                        return new MemoryStream(LZ4Uncompress(buffer, @in.UncompressedSize));
                }
            offset = @in.Offset;
            return @in;
        }

        #endregion Methods
    }
}