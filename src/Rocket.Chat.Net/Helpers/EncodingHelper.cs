namespace Rocket.Chat.Net.Helpers
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    public static class EncodingHelper
    {
        public const string Sha256 = "sha-256";
        public static StringBuilder builder = new StringBuilder();

        public static string Sha256Hash(string value)
        {
            builder.Clear();

            using (var hash = SHA256.Create())
            {
                var result = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
                foreach (var b in result)
                    builder.Append(b.ToString("x2"));

            }
            return builder.ToString();
        }

        public static string ConvertToBase64(Stream stream)
        {
            using (var bufferStream = new MemoryStream())
            {
                stream.CopyTo(bufferStream);
                return Convert.ToBase64String(bufferStream.ToArray());
            }
        }
    }
}