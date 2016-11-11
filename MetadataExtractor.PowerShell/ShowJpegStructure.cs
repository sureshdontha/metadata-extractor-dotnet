﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Management.Automation;
using JetBrains.Annotations;
using MetadataExtractor.Formats.Jpeg;
using System.Linq;
using System.Text;
using MetadataExtractor.Formats.Adobe;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Jfif;
using MetadataExtractor.Formats.Jfxx;
using MetadataExtractor.Formats.Photoshop;
using MetadataExtractor.Formats.Xmp;
using MetadataExtractor.Util;

namespace MetadataExtractor.PowerShell
{
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public struct JpegSegment
    {
        public JpegSegmentType Type { get; }
        public int Length { get; }
        public int Padding { get; set; }
        public long Offset { get; }
        public string Preamble { get; }

        public JpegSegment(JpegSegmentType type, int length, int padding, long offset, string preamble)
        {
            Type = type;
            Length = length;
            Padding = padding;
            Offset = offset;
            Preamble = preamble;
        }
    }

    [Cmdlet(VerbsCommon.Show, "JpegStructure")]
    [UsedImplicitly]
    public sealed class ShowJpegStructure : PSCmdlet
    {
        private static readonly ByteTrie<string> _appSegmentByPreambleBytes;

        static ShowJpegStructure()
        {
            _appSegmentByPreambleBytes = new ByteTrie<string>();

            // TODO make all these preamble properties have the same name -- maybe an abstraction
            _appSegmentByPreambleBytes.AddPath("Adobe", Encoding.UTF8.GetBytes(AdobeJpegReader.JpegSegmentPreamble));
            _appSegmentByPreambleBytes.AddPath("Ducky", Encoding.UTF8.GetBytes(DuckyReader.JpegSegmentPreamble));
            _appSegmentByPreambleBytes.AddPath("Exif", Encoding.UTF8.GetBytes(ExifReader.JpegSegmentPreamble));
            _appSegmentByPreambleBytes.AddPath("ICC", Encoding.UTF8.GetBytes(IccReader.JpegSegmentPreamble));
            _appSegmentByPreambleBytes.AddPath("JFIF", Encoding.UTF8.GetBytes(JfifReader.JpegSegmentPreamble));
            _appSegmentByPreambleBytes.AddPath("JFXX", Encoding.UTF8.GetBytes(JfxxReader.JpegSegmentPreamble));
            _appSegmentByPreambleBytes.AddPath("Photoshop", Encoding.UTF8.GetBytes(PhotoshopReader.JpegSegmentPreamble));
            _appSegmentByPreambleBytes.AddPath("XMP", Encoding.UTF8.GetBytes(XmpReader.JpegSegmentPreamble));
        }

        [Parameter(Position = 0, Mandatory = true, HelpMessage = "Path to the file to process")]
        [ValidateNotNullOrEmpty]
        public string FilePath { get; set; }

        protected override void ProcessRecord()
        {
            base.ProcessRecord();

            WriteVerbose($"Extracting metadata from file: {FilePath}");

            using (var stream = File.OpenRead(FilePath))
                WriteObject(ReadSegments(stream).ToList());
        }

        [NotNull]
        public static IEnumerable<JpegSegment> ReadSegments([NotNull] Stream stream)
        {
            if (!stream.CanSeek)
                throw new ArgumentException("Must be able to seek.", nameof(stream));

            // first two bytes should be JPEG magic number
            var magicNumber = GetUInt16(stream);

            if (magicNumber != 0xFFD8)
                throw new JpegProcessingException($"JPEG data should begin with 0xFFD8, not 0x{magicNumber:X4}.");

            while (true)
            {
                var padding = 0;

                // Find the segment marker. Markers are zero or more 0xFF bytes, followed
                // by a 0xFF and then a byte not equal to 0x00 or 0xFF.
                var segmentIdentifier = stream.ReadByte();
                var segmentTypeByte = stream.ReadByte();

                if (segmentTypeByte == -1)
                    yield break;

                // Read until we have a 0xFF byte followed by a byte that is not 0xFF or 0x00
                while (segmentIdentifier != 0xFF || segmentTypeByte == 0xFF || segmentTypeByte == 0)
                {
                    padding++;
                    segmentIdentifier = segmentTypeByte;
                    segmentTypeByte = stream.ReadByte();

                    if (segmentTypeByte == -1)
                        yield break;
                }

                var segmentType = (JpegSegmentType)segmentTypeByte;
                var offset = stream.Position - 2;

                // if there is a payload, then segment length includes the two size bytes
                if (segmentType.ContainsPayload())
                {
                    var pos = stream.Position;

                    // Read the 2-byte big-endian segment length (excludes two marker bytes)
                    var b1 = stream.ReadByte();
                    var b2 = stream.ReadByte();
                    if (b2 == -1)
                        yield break;
                    var segmentLength = unchecked((ushort)(b1 << 8 | b2));

                    var preambleBytes = new byte[Math.Min(segmentLength, _appSegmentByPreambleBytes.MaxDepth)];
                    if (stream.Read(preambleBytes, 0, preambleBytes.Length) != preambleBytes.Length)
                        yield break;
                    var preamble = _appSegmentByPreambleBytes.Find(preambleBytes);

                    yield return new JpegSegment(segmentType, segmentLength, padding, offset, preamble);

                    // A length of less than two would be an error
                    if (segmentLength < 2)
                        yield break;

                    stream.Position = pos + segmentLength;
                }
                else
                {
                    yield return new JpegSegment(segmentType, 0, padding, offset, "");
                }
            }
        }

        private static byte GetByte(Stream stream)
        {
            var b = stream.ReadByte();
            if (b == -1)
                throw new IOException("Unexpected end of stream.");
            return unchecked((byte)b);
        }

        private static ushort GetUInt16(Stream stream)
        {
            var b1 = stream.ReadByte();
            var b2 = stream.ReadByte();
            if (b2 == -1)
                throw new IOException("Unexpected end of stream.");
            return unchecked((ushort)(b1 << 8 | b2));
        }
    }

}