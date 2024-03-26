// Copyright (C) Dmitry Yakimenko (detunized@gmail.com).
// Licensed under the terms of the MIT license. See LICENCE for details.

#nullable enable

using System;
using FluentAssertions;
using PasswordManagerAccess.Common;
using PasswordManagerAccess.ProtonPass;
using Xunit;

namespace PasswordManagerAccess.Test.ProtonPass
{
    public class SrpTest
    {
        [Fact]
        public void ParseModulus_parses_base64_url_encoded_string()
        {
            // Arrange
            var expected = Modulus.Decode64();

            // Act
            var modulus = Srp.ParseModulus(ModulusMessage);

            // Assert
            modulus.Should().Equal(expected);
        }

        [Theory]
        [InlineData("-----BEGIN PGP SIGNED MESSAGE-----", "?----BEGIN PGP SIGNED MESSAGE-----", "Invalid PGP message format: missing start *")]
        [InlineData("-----BEGIN PGP SIGNATURE-----", "?----BEGIN PGP SIGNATURE-----", "Invalid PGP message format: missing start *")]
        [InlineData("\n\n", "\n", "Invalid PGP message format: missing two *")]
        public void ParseModulus_throws_on_error(string replaceWhat, string replaceWith, string errorMessage)
        {
            // Arrange
            var brokenMessage = ModulusMessage.Replace(replaceWhat, replaceWith);

            // Act
            Action act = () => Srp.ParseModulus(brokenMessage);

            // Assert
            act.Should().Throw<InternalErrorException>().WithMessage(errorMessage);
        }

        //
        // Data
        //

        private const string Modulus =
            "A5AwfkcWr2Sq7Wy8hpOHAnAFo8hZdKsVmInqvOckcHaeV36YPTK4H7yfE5cUtkHaL/MaPl1J5expZ9x/mXkAjsTicSXEi3iAAaBa4CrW" +
            "Yydjm29ESeejdwwsR9M/FprqvYkJ1Nb6VzhKr2ZvjPKiw2UK1N0PcuYlf+0fxOXOH0vW0aXBk0nKu1vlloqTVoYUkdevAs1eL2bCjS0d" +
            "gECy2QxqNdNj9/uhDVshxEJxl1wyAPvLG0Eq7XWmRGyA3pgEDp3IwhTVQJ+BfawJf+vN/Q4tEOtTAhCFCCpL59bFw5fTehKrMJ6cfAfd" +
            "QIrqiVygbW5FRevLqQVX/YlIYghn0g==";

        private const string ModulusMessage =
            "-----BEGIN PGP SIGNED MESSAGE-----\n" +
            "Hash: SHA256\n" +
            "\n" +
            Modulus + "\n" +
            "-----BEGIN PGP SIGNATURE-----\n" +
            "Version: ProtonMail\n" +
            "Comment: https://protonmail.com\n" +
            "\n" +
            "wl4EARYIABAFAlwB1jwJEDUFhcTpUY8mAABhWgEAypod4Gzxqy1RoZhVMG5a\n" +
            "Tnbwx8xwdYwmvqq7cPHKBrYA/0+eOtSjHOA95MjC8aq1v5XOsHhbxnnSvPGJ\n" +
            "Z/+kS6gI\n" +
            "=ARO0\n" +
            "-----END PGP SIGNATURE-----\n";
    }
}
