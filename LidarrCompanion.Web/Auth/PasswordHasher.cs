using System.Security.Cryptography;

namespace LidarrCompanion.Web.Auth
{
    // Salted PBKDF2 hashing for the single shared admin password. No external dependency -
    // Rfc2898DeriveBytes.Pbkdf2 and CryptographicOperations are both built into .NET.
    public static class PasswordHasher
    {
        private const int SaltSizeBytes = 16;
        private const int KeySizeBytes = 32;
        private const int Iterations = 210_000;

        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySizeBytes);
            return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(key)}";
        }

        public static bool Verify(string password, string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return false;

            var parts = stored.Split(':');
            if (parts.Length != 2) return false;

            try
            {
                var salt = Convert.FromBase64String(parts[0]);
                var expected = Convert.FromBase64String(parts[1]);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }
    }
}
