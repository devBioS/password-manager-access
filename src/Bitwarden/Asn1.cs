// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

using System.Collections.Generic;
using System.IO;
using PasswordManagerAccess.Common;

namespace PasswordManagerAccess.Bitwarden
{
    // Very-very basic ASN.1 parser. Just enough to extract the RSA key
    // parameters stored in a vault. Supports only sequences, octet strings
    // and numbers. Error handling is minimal too.
    static class Asn1
    {
        public enum Kind
        {
            Integer,
            OctetString,
            Null,
            Sequence,
        }

        public static KeyValuePair<Kind, byte[]> ExtractItem(byte[] bytes)
        {
            return bytes.Open(reader => ExtractItem(reader));
        }

        public static KeyValuePair<Kind, byte[]> ExtractItem(BinaryReader reader)
        {
            var id = reader.ReadByte();
            var tag = id & 0x1F;

            Kind kind = tag switch
            {
                2 => Kind.Integer,
                4 => Kind.OctetString,
                5 => Kind.Null,
                16 => Kind.Sequence,
                _ => throw new InternalErrorException($"Unknown ASN.1 tag {tag}")
            };

            int size = reader.ReadByte();
            if ((size & 0x80) != 0)
            {
                var sizeLength = size & 0x7F;
                size = 0;
                for (var i = 0; i < sizeLength; ++i)
                {
                    var oneByte = reader.ReadByte();
                    size = size * 256 + oneByte;
                }
            }

            var payload = reader.ReadBytes(size);

            return new KeyValuePair<Kind, byte[]>(kind, payload);
        }
    }
}
