using System.Text.RegularExpressions;

namespace LostAndFoundApi.Models
{
    /// <summary>Editable fields of a user's own profile. Email is checked for uniqueness.</summary>
    public class ProfileUpdate
    {
        public string? firstName { get; set; }
        public string? lastName { get; set; }
        public string? email { get; set; }
        public string? phone { get; set; }
        public string? secondaryPhone { get; set; }
        public string? address { get; set; }
        public string? city { get; set; }
    }

    public class ChangePasswordRequest
    {
        public string? currentPassword { get; set; }
        public string? newPassword { get; set; }
    }

    /// <summary>
    /// The parts of a lost or found report its owner may change. Contact details are
    /// not here on purpose: they always come from the owner's profile.
    /// </summary>
    public class ItemUpdate
    {
        public string? itemName { get; set; }
        public string? category { get; set; }
        public string? location { get; set; }
        public string? description { get; set; }
        public DateTime? date { get; set; }
        public string? timeLost { get; set; }
        public string? brand { get; set; }
        public string? color { get; set; }
        public string? reward { get; set; }
        public string? detectedCategory { get; set; }
        public double? detectedConfidence { get; set; }
    }

    /// <summary>
    /// Server-side copies of the rules the forms enforce. The forms are a convenience;
    /// these are what actually protect the data, since anyone can call the API directly.
    /// Each returns null when the value is fine, or the sentence to show the user.
    /// </summary>
    public static class InputRules
    {
        private static readonly Regex EmailPattern =
            new(@"^[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}$", RegexOptions.Compiled);

        private static readonly Regex BdPhonePattern = new(@"^(?:\+?880|0)1[3-9]\d{8}$", RegexOptions.Compiled);
        private static readonly Regex NamePattern = new(@"^[A-Za-z][A-Za-z .'-]*$", RegexOptions.Compiled);

        public static string? Name(string? value, string label)
        {
            var v = (value ?? "").Trim();
            if (v.Length < 2 || v.Length > 50) return $"{label} must be between 2 and 50 characters.";
            if (!NamePattern.IsMatch(v)) return $"{label} can only contain letters.";
            return null;
        }

        public static string? Email(string? value)
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) return "Email is required.";
            if (v.Length > 100 || !EmailPattern.IsMatch(v) || v.Contains("..")) return "Enter a valid email address.";
            return null;
        }

        public static string? Phone(string? value, string label, bool required)
        {
            var v = Regex.Replace(value ?? "", @"[\s\-()]", "");
            if (v.Length == 0) return required ? $"{label} is required." : null;
            return BdPhonePattern.IsMatch(v) ? null : $"{label} must be a valid Bangladeshi mobile number, e.g. 01712345678.";
        }

        public static string? Text(string? value, string label, int min, int max)
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) return $"{label} is required.";
            if (v.Length < min) return $"{label} must be at least {min} characters.";
            if (v.Length > max) return $"{label} must be under {max} characters.";
            return null;
        }

        public static string? Password(string? value)
        {
            var v = value ?? "";
            if (v.Length < 8) return "Password must be at least 8 characters.";
            if (v.Length > 128) return "Password is too long.";
            if (!v.Any(char.IsUpper) || !v.Any(char.IsLower) || !v.Any(char.IsDigit) || v.All(char.IsLetterOrDigit))
                return "Password needs an uppercase letter, a lowercase letter, a number and a symbol.";
            return null;
        }

        /// <summary>A lost/found date: not in the future (a day's grace for time zones), not absurdly old.</summary>
        public static string? ItemDate(DateTime date)
        {
            if (date.Date > DateTime.UtcNow.Date.AddDays(1)) return "The date cannot be in the future.";
            if (date < DateTime.UtcNow.AddYears(-5)) return "The date cannot be more than 5 years ago.";
            return null;
        }

        public static string? FirstError(params string?[] errors) => errors.FirstOrDefault(e => e is not null);
    }
}
